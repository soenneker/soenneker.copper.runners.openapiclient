import fs from 'node:fs/promises';
import { convert } from '@scalar/postman-to-openapi';

const [, , collectionPath, outputPath] = process.argv;

if (!collectionPath || !outputPath) {
  throw new Error('Usage: node convert-postman.mjs <collection-path> <output-path>');
}

const collection = await fs.readFile(collectionPath, 'utf8');
const openApi = await convert(collection);

openApi.servers = [{ url: 'https://api.copper.com/developer_api/v1' }];

await fs.writeFile(outputPath, `${JSON.stringify(openApi, null, 2)}\n`);
