@echo off
REM Windows 启动包装 —— 双击本文件即可
cd /d "%~dp0"
where py >nul 2>nul && ( py tui.py & goto :eof )
where python >nul 2>nul && ( python tui.py & goto :eof )
echo 找不到 Python，请先安装 Python 3
pause
