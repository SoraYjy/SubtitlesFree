#!/usr/bin/env bash
# 打包「绿色文件夹」分发版：self-contained 单文件 exe + engine 脚本 + 内嵌 Python runtime。
# 不含 Python 依赖与模型（用户双击 安装依赖.bat 装依赖，模型见 README）。产出 dist/SubtitlesFree/。
set -e
cd "$(dirname "$0")"

OUT=dist/SubtitlesFree
echo "[1/4] publish（self-contained · single-file · win-x64）..."
# 清空即可；顶层目录本体有时被反作弊/杀软扫描句柄占住删不掉（Device or resource busy），
# 内容删净后复用该目录，不影响产物正确性
rm -rf "$OUT" 2>/dev/null || true
rm -rf "$OUT"/* 2>/dev/null || true
mkdir -p "$OUT"
dotnet publish src/SubtitlesFree.App -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -o "$OUT"

echo "[2/4] 复制 engine 脚本（不含 venv/samples/模型）+ 内置 ffmpeg..."
mkdir -p "$OUT/engine"
cp engine/*.py "$OUT/engine/"
cp engine/requirements*.txt "$OUT/engine/"
# ffmpeg 随仓库分发（engine/ffmpeg.exe），放 exe 旁——引擎启动时由 GUI 前置到子进程 PATH
cp engine/ffmpeg.exe "$OUT/ffmpeg.exe"

echo "[3/4] 组装 runtime/（内嵌 Python 3.11，pip 预装，依赖留给用户侧 bat）..."
mkdir -p "$OUT/runtime"
unzip -q -o engine/embed/python-3.11.9-embed-amd64.zip -d "$OUT/runtime"
# 嵌入式默认全隔离（不吃 site-packages）：追加这两行让 pip 装的包可被 import（ComfyUI 便携版同配方）
printf 'python311.zip\n.\nLib\\site-packages\nimport site\n' > "$OUT/runtime/python311._pth"
# pack 机一次性联网预装 pip（用户侧 bat 只装依赖，不做 pip 引导）
"$OUT/runtime/python.exe" engine/embed/get-pip.py -i https://mirrors.aliyun.com/pypi/simple/ --no-warn-script-location -q

echo "[4/4] 生成 安装依赖.bat ..."
# torch 不在 lock（+cu124 本地版本号不在普通镜像），由命令 2 单独锁版本安装。
# bat 文案必须纯 ASCII：中文无论 UTF-8 还是 GBK 都会撞上 cmd 批处理行解析的字节错位/
# 代码页继承差异（echo 行被截断当命令执行，有弄乱后续命令行的风险）——设计 §4.3 的兜底预案。
# CRLF 必须保留（LF 行尾下 goto/label 在部分 Windows 失灵）
cat > "$OUT/安装依赖.bat" <<'EOF'
@echo off
cd /d "%~dp0"
title SubtitlesFree Dependency Installer
echo ================================================
echo  First-time install: downloads about 3.5 GB (10-40 min).
echo  No interaction needed. Wait for "INSTALL OK".
echo ================================================
echo [1/3] Upgrading pip ...
runtime\python.exe -m pip install -U pip -i https://mirrors.aliyun.com/pypi/simple/ || goto fail
echo [2/3] Installing torch (CUDA, about 2.5 GB, the slowest part) ...
rem Official cu124 index: the path real users have used since v1.0.0.
rem Aliyun flat mirror is throttled (~0.2 MB/s); SJTU page is not pip-parseable for +cu124.
runtime\python.exe -m pip install torch==2.6.0+cu124 --index-url https://download.pytorch.org/whl/cu124 || goto fail
echo [3/3] Installing engine dependencies (whisperx etc.) ...
runtime\python.exe -m pip install -r engine\requirements.lock.txt -i https://mirrors.aliyun.com/pypi/simple/ || goto fail
echo.
echo ================================================
echo  INSTALL OK! Open SubtitlesFree and click "Check Environment".
echo ================================================
pause
exit /b 0
:fail
echo.
echo  INSTALL FAILED (most likely a network hiccup). Close this window and
echo  double-click this file again to resume; finished parts are skipped.
echo  If it keeps failing, screenshot this window and send it to the author.
pause
exit /b 1
EOF
sed -i 's/\r\?$/\r/' "$OUT/安装依赖.bat"

echo "完成。"
echo "产出：$OUT  （$(du -sh "$OUT" | cut -f1)）"
