// Local-only SVG -> PNG reference export. Install resvg under artifacts/render-tools.
// Does not start Rhino, a browser, or a server; source SVGs are unchanged.
const fs = require('node:fs');
const path = require('node:path');
const { Resvg } = require(path.resolve('artifacts/render-tools/node_modules/@resvg/resvg-js'));
for (const filename of process.argv.slice(2)) {
  let svg = fs.readFileSync(filename, 'utf8');
  const box = svg.match(/viewBox="([^"]+)"/)[1].split(/\s+/).map(Number);
  const scale = Math.min(1200 / box[2], 800 / box[3]);
  // resvg does not implement non-scaling-stroke. Express the pixel stroke
  // weights in view coordinates explicitly for this fixed-resolution export.
  svg = svg.replace(/vector-effect:non-scaling-stroke;?/g, '')
    .replace(/stroke-width:([\d.]+)/g, (_, width) => `stroke-width:${Number(width) / scale}`)
    .replace(/stroke-dasharray:4 3/g, `stroke-dasharray:${4 / scale} ${3 / scale}`)
    .replace('<svg ', '<svg width="1200" height="800" ');
  const output = filename.replace(/\.svg$/i, '.png');
  fs.writeFileSync(output, new Resvg(svg, { background: 'white' }).render().asPng());
  console.log(output);
}
