#!/usr/bin/env bash
# 打包「绿色文件夹」分发版：self-contained 单文件 exe + engine 脚本。
# 不含 Python 环境与模型（自备，见 README）。产出 dist/SubtitlesFree/。
set -e
cd "$(dirname "$0")"

OUT=dist/SubtitlesFree
echo "[1/3] publish（self-contained · single-file · win-x64）..."
rm -rf "$OUT"
dotnet publish src/SubtitlesFree.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -o "$OUT"

echo "[2/3] 复制 engine 脚本（不含 venv/samples/模型）..."
mkdir -p "$OUT/engine"
cp engine/*.py "$OUT/engine/"
cp engine/requirements*.txt "$OUT/engine/"

echo "[3/3] 完成。"
echo "产出：$OUT  （$(du -sh "$OUT" | cut -f1)）"
