// Makes the two photos of spec 0018 (Look) from the downloaded Unsplash file, run once by hand:
//   node scripts/make-photos.mjs <downloaded.jpg>
// city-dark.webp: the photo as it is, cropped to cover 2200 x 1300. city-light.webp: the same, grayscale, inverted and
// contrast 1.15 around the middle grey (1.15 * x - 19.2). WebP quality from 82 down in steps of 4 until the file is at
// most 550 KB. The files stay untinted: the CSS tints them.
import { mkdirSync, statSync } from 'node:fs';
import sharp from 'sharp';

const LIMIT = 550 * 1000;
const OUT = 'src/assets/photos/';
const source = process.argv[2];
if (!source) {
  console.error('usage: node scripts/make-photos.mjs <downloaded.jpg>');
  process.exit(2);
}
mkdirSync(OUT, { recursive: true });

const cropped = () => sharp(source).rotate().resize(2200, 1300, { fit: 'cover', position: 'centre' });

async function write(name, pipeline) {
  for (let quality = 82; quality >= 50; quality -= 4) {
    await pipeline().webp({ quality }).toFile(OUT + name);
    const size = statSync(OUT + name).size;
    if (size <= LIMIT) {
      console.log(name + ': 2200 x 1300, quality ' + quality + ', ' + size + ' bytes');
      return;
    }
  }
  throw new Error(name + ': more than 550 KB even at quality 50');
}

await write('city-dark.webp', cropped);
await write('city-light.webp', () => cropped().grayscale().negate({ alpha: false }).linear(1.15, -19.2));
