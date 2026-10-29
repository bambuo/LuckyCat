// 把多个 PNG 打包成 ICO；PNG 压缩的 ICO 合法且为 Win10+ 支持。
import { readFileSync, writeFileSync } from 'node:fs'
import { join } from 'node:path'

const [workDir, outFile, ...sizes] = process.argv.slice(2)
if (!workDir || !outFile || sizes.length === 0) {
  console.error('usage: node make-ico.mjs <png-dir> <out.ico> <size...>')
  process.exit(1)
}

const images = sizes.map((size) => ({
  size: Number(size),
  data: readFileSync(join(workDir, `app-${size}.png`)),
}))

const header = Buffer.alloc(6)
header.writeUInt16LE(0, 0)
header.writeUInt16LE(1, 2)
header.writeUInt16LE(images.length, 4)

const entries = Buffer.alloc(16 * images.length)
let offset = header.length + entries.length

images.forEach((image, index) => {
  const at = index * 16
  // 目录项中 0 表示 256，其余尺寸直接写入
  entries.writeUInt8(image.size >= 256 ? 0 : image.size, at)
  entries.writeUInt8(image.size >= 256 ? 0 : image.size, at + 1)
  entries.writeUInt8(0, at + 2)
  entries.writeUInt8(0, at + 3)
  entries.writeUInt16LE(1, at + 4)
  entries.writeUInt16LE(32, at + 6)
  entries.writeUInt32LE(image.data.length, at + 8)
  entries.writeUInt32LE(offset, at + 12)
  offset += image.data.length
})

writeFileSync(outFile, Buffer.concat([header, entries, ...images.map((image) => image.data)]))
console.log(`ico written: ${outFile}`)
