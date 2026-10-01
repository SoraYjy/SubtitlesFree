#!/usr/bin/env bash
# 打包「绿色文件夹」分发版：self-contained 单文件 exe + engine 脚本 + 内嵌 Python runtime。
# 不含 Python 依赖与模型（用户双击 安装依赖.bat 装依赖，模型见 README）。产出 dist/SubtitlesFree/。
set -e
cd "$(dirname "$0")"

OUT=dist/SubtitlesFree
echo "[1/4] publish（self-contained · single-file · win-x64）..."
rm -rf "$OUT"
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
# torch 不在 lock（+cu124 本地版本号不在普通镜像），由命令 2 单独锁版本安装
cat > "$OUT/安装依赖.bat" <<'EOF'
@echo off
chcp 65001 >nul
cd /d "%~dp0"
title SubtitlesFree 依赖安装
echo ================================================
echo  首次安装约需下载 3.5GB，视网速 10~40 分钟。
echo  全程无需操作，装完窗口会显示「安装完成」。
echo ================================================
echo [1/3] 升级 pip ...
runtime\python.exe -m pip install -U pip -i https://mirrors.aliyun.com/pypi/simple/ || goto fail
echo [2/3] 安装 torch（CUDA 版，约 2.5GB，最耗时）...
runtime\python.exe -m pip install torch==2.6.0+cu124 -f https://mirror.sjtu.edu.cn/pytorch-wheels/cu124/ -i https://mirrors.aliyun.com/pypi/simple/ || goto fail
echo [3/3] 安装引擎依赖（whisperx 等）...
runtime\python.exe -m pip install -r engine\requirements.lock.txt -i https://mirrors.aliyun.com/pypi/simple/ || goto fail
echo.
echo ================================================
echo  安装完成！回到 SubtitlesFree 点「检测环境」。
echo ================================================
pause
exit /b 0
:fail
echo.
echo  安装出错（多为网络中断）。关掉本窗口再双击一次即可重试，
echo  已装好的部分会自动跳过。若反复失败，请把本窗口截图发给作者。
pause
exit /b 1
EOF
# bat 必须 CRLF（LF 行尾下 goto/label 在部分 Windows 失灵）；UTF-8 无 BOM 由 bash 天然保证
sed -i 's/\r\?$/\r/' "$OUT/安装依赖.bat"

echo "完成。"
echo "产出：$OUT  （$(du -sh "$OUT" | cut -f1)）"
