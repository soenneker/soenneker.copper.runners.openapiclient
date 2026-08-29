using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Soenneker.Extensions.String;
using Soenneker.Git.Util.Abstract;
using Soenneker.Copper.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Environment;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.ValueTask;
using Soenneker.Kiota.Util.Abstract;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.File.Download.Abstract;
using System.Collections.Generic;
using System.Diagnostics;

namespace Soenneker.Copper.Runners.OpenApiClient.Utils;

/// <inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IConfiguration _configuration;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IKiotaUtil _kiotaUtil;
    private readonly IOpenApiFixer _openApiFixer;
    private readonly IFileDownloadUtil _fileDownloadUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;
    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IConfiguration configuration, IGitUtil gitUtil, IDotnetUtil dotnetUtil,
        IFileDownloadUtil fileDownloadUtil, IFileUtil fileUtil, IDirectoryUtil directoryUtil, IKiotaUtil kiotaUtil, IOpenApiFixer openApiFixer)
    {
        _logger = logger;
        _configuration = configuration;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _kiotaUtil = kiotaUtil;
        _openApiFixer = openApiFixer;
        _fileDownloadUtil = fileDownloadUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}", cancellationToken: cancellationToken);

        string targetFilePath = Path.Combine(gitDirectory, "openapi.json");

        await _fileUtil.DeleteIfExists(targetFilePath, cancellationToken: cancellationToken);

        string openApiDocumentUrl = _configuration["Copper:ClientGenerationUrl"] ?? "https://developer.copper.com/download/copper_postman_collection.json";

        string? filePath = await _fileDownloadUtil.Download(openApiDocumentUrl,
            targetFilePath, fileExtension: ".json", cancellationToken: cancellationToken);

        if (filePath == null)
            throw new InvalidOperationException("Copper OpenAPI document download failed.");

        string convertedFilePath = Path.Combine(gitDirectory, "openapi.converted.json");
        await _fileUtil.DeleteIfExists(convertedFilePath, cancellationToken: cancellationToken);
        await ConvertPostmanCollection(filePath, convertedFilePath, cancellationToken);
        filePath = convertedFilePath;

        string fixedFilePath = Path.Combine(gitDirectory, "openapi.fixed.json");
        await _fileUtil.DeleteIfExists(fixedFilePath, cancellationToken: cancellationToken);
        await _openApiFixer.Fix(filePath, fixedFilePath, cancellationToken).NoSync();

        await _kiotaUtil.EnsureInstalled(cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src", Constants.Library);

        await DeleteAllExceptCsproj(srcDirectory, cancellationToken);

        await _kiotaUtil.Generate(fixedFilePath, "CopperOpenApiClient", Constants.Library, gitDirectory, cancellationToken).NoSync();

        await BuildAndPush(gitDirectory, cancellationToken).NoSync();
    }

    private static async ValueTask ConvertPostmanCollection(string collectionPath, string outputPath, CancellationToken cancellationToken)
    {
        string converterDirectory = Path.Combine(AppContext.BaseDirectory, "Converter");
        string npm = OperatingSystem.IsWindows() ? ResolveFromPath("npm.cmd") : "npm";

        await RunProcess(npm, ["ci", "--prefix", converterDirectory, "--no-audit", "--no-fund"], cancellationToken);
        await RunProcess("node", [Path.Combine(converterDirectory, "convert-postman.mjs"), collectionPath, outputPath], cancellationToken);
    }

    private static string ResolveFromPath(string fileName)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");

        foreach (string directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = Path.Combine(directory, fileName);

            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not locate {fileName} on PATH.");
    }

    private static async ValueTask RunProcess(string fileName, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process {StartInfo = startInfo};
        process.Start();

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        string output = await standardOutput;
        string error = await standardError;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{fileName} failed with exit code {process.ExitCode}: {error}\n{output}");
    }

    /// <summary>
    /// Deletes generated files beneath the directory while preserving C# project files.
    /// </summary>
    /// <param name="directoryPath">Root directory whose generated contents should be removed.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes after the targeted files have been deleted.</returns>
    public async ValueTask DeleteAllExceptCsproj(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!(await _directoryUtil.Exists(directoryPath, cancellationToken)))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return;
        }

        try
        {
            // Delete all files except .csproj
            List<string> files = await _directoryUtil.GetFilesByExtension(directoryPath, "", true, cancellationToken);
            foreach (string file in files)
            {
                if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _fileUtil.Delete(file, ignoreMissing: true, log: false, cancellationToken);
                        _logger.LogInformation("Deleted file: {FilePath}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete file: {FilePath}", file);
                    }
                }
            }

            // Delete all empty subdirectories
            List<string> dirs = await _directoryUtil.GetAllDirectoriesRecursively(directoryPath, cancellationToken);
            foreach (string dir in dirs.OrderByDescending(d => d.Length)) // Sort by depth to delete from deepest first
            {
                try
                {
                    List<string> dirFiles = await _directoryUtil.GetFilesByExtension(dir, "", false, cancellationToken);
                    List<string> subDirs = await _directoryUtil.GetAllDirectories(dir, cancellationToken);
                    if (dirFiles.Count == 0 && subDirs.Count == 0)
                    {
                        await _directoryUtil.Delete(dir, cancellationToken);
                        _logger.LogInformation("Deleted empty directory: {DirectoryPath}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete directory: {DirectoryPath}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while cleaning the directory: {DirectoryPath}", directoryPath);
        }
    }

    private async ValueTask BuildAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");
        string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
        string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, name, email, cancellationToken);
    }
}
