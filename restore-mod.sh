#!/bin/bash
# 把 Mod 恢复到游戏目录（纯原版 → 可玩蜂群版）
set -e
GAME=$(readlink -f "$HOME/.local/share/Steam/steamapps/common/Among Us")
SRC="$HOME/AmongUsMods/releases/v0.4.0-hivemind-20261003-2010"
[ -d "$SRC" ] || { echo "❌ 找不到归档: $SRC"; exit 1; }

echo "⚠️  本脚本只恢复插件 DLL，不恢复 BepInEx 框架。"
echo "    若你已删除 BepInEx/ winhttp.dll 等，需先重装 BepInEx 6 IL2CPP。"
mkdir -p "$GAME/BepInEx/plugins"
cp "$SRC/SmartLocal.dll" "$GAME/BepInEx/plugins/SmartLocal.dll"
echo "✅ 已恢复 SmartLocal.dll"
echo
echo "还需要手动做两件事："
echo "  1. Steam → Among Us → 属性 → 启动选项，填入:"
echo '       WINEDLLOVERRIDES="winhttp=n,b" %command%'
echo "  2. （或）在 Wine 前缀注册表写 DLL 覆写:"
echo "       compatdata/945360/pfx/user.reg 的 [Software\\\\Wine\\\\DllOverrides] 节加入"
echo '       "winhttp"="native,builtin"'
