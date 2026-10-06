#!/bin/bash
# ═══════════════════════════════════════════════════════════════
# 测试前自检 —— 避免在「被污染」的房主上做测试
#
# 背景（用户反馈）：
#   每次测试失败，房主都会留下一个「没有名字的幽灵玩家条目」；
#   累积几个之后房主的状态机就会崩，把用户自己踢出游戏。
#   而 AI 写命令时看不到游戏画面，经常撞在污染状态上。
#
# 这个脚本的作用：**脏了就不测**，直接告诉用户重开一局。
#
# 用法：
#   ./test-bot.sh          # 自检后启动 bot（默认参数）
#   ./test-bot.sh 1        # 自检后启动，颜色=1(BLUE)
# ═══════════════════════════════════════════════════════════════
set -u

GAME=$(readlink -f "$HOME/.local/share/Steam/steamapps/common/Among Us")
LOG="$GAME/BepInEx/LogOutput.log"
DIR="$(cd "$(dirname "$0")" && pwd)"

export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "════════ 测试前自检 ════════"

# ── 1. 游戏进程 ──
if ! pgrep -x "Among Us.exe" >/dev/null 2>&1; then
    echo "  ❌ 游戏未运行 —— 请先启动游戏"
    exit 1
fi
echo "  ✅ 游戏运行中"

# ── 2. 是否在房间里 ──
if ! ss -ulpn 2>/dev/null | grep -q ':22023'; then
    echo "  ❌ 不在房间里 —— 请：本地 → 创建游戏 → 停在等待大厅"
    exit 2
fi
echo "  ✅ 监听 22023（在房间里）"

# ── 3. 玩家表是否干净（关键）──
LAST=$(grep -a '玩家表' "$LOG" 2>/dev/null | tail -1)
if [ -z "$LAST" ]; then
    echo "  ⚠️  日志里还没有玩家表记录（可能是刚重启，等 5 秒再看）"
    echo "     如果游戏刚启动，这是正常的 —— 继续测试"
else
    COUNT=$(echo "$LAST" | grep -oP '玩家表\(\K[0-9]+')
    echo "  当前玩家表: ${COUNT:-?} 条"
    if [ "${COUNT:-0}" != "1" ]; then
        echo
        echo "  ⛔ 玩家表不干净（应为 1，实际 ${COUNT}）—— 拒绝测试"
        echo "     里面是之前失败测试留下的幽灵条目。"
        echo "     它们会让房主的状态机崩溃、把你自己踢出游戏。"
        echo
        echo "  👉 请手动操作：退出到主菜单 → 重新「本地创建游戏」→ 再跑一次本脚本"
        echo
        echo "  当前内容:"
        echo "$LAST" | sed 's/.*玩家表/    /'
        exit 3
    fi
    echo "  ✅ 玩家表干净（只有房主自己）"
fi

# ── 4. 补丁是否挂上（避免「工具坏了」被误读成「事实如此」）──
if ! grep -aq 'PlayerInfoWatch 已挂载' "$LOG" 2>/dev/null; then
    echo "  ⚠️  PlayerInfoWatch 未挂载 —— 观测数据可能不可信"
fi

# ── 5. 清掉遗留 bot ──
n=0
for d in /proc/[0-9]*; do
    pid=${d#/proc/}
    [ "$pid" = "$$" ] && continue
    [ -r "$d/cmdline" ] || continue
    # ★ 必须同时满足：可执行文件是 dotnet，且参数里有 BotClient.dll
    #   只匹配 'BotClient' 会把执行命令的 shell 自己也杀掉（踩过两次）。
    exe=$(tr '\0' '\n' < "$d/cmdline" 2>/dev/null | head -1)
    case "$exe" in
        */dotnet|dotnet) ;;
        *) continue ;;
    esac
    if tr '\0' ' ' < "$d/cmdline" 2>/dev/null | grep -q 'BotClient.dll'; then
        kill "$pid" 2>/dev/null && n=$((n+1))
    fi
done
[ "$n" -gt 0 ] && echo "  🧹 已清理 $n 个遗留 bot"

# ── 6. 启动 ──
# 用法: test-bot.sh [颜色] [playerId] [名字] [日志文件]
COLOR="${1:-1}"
PID_ARG="${2:-1}"
NAME="${3:-BotTest}"
LOGF="${4:-/tmp/bot.log}"
echo
echo "════════ 启动 bot ════════"
echo "  名字=$NAME  颜色=$COLOR  playerId=$PID_ARG  日志=$LOGF"
cd "$DIR"
# 参数顺序: gameId version user seconds _rpcNetId _netBase color forcePid rpcTarget
nohup dotnet bin/Release/net6.0/BotClient.dll 32 50663600 "$NAME" 0 0 8 "$COLOR" "$PID_ARG" > "$LOGF" 2>&1 &
echo $! > "/tmp/bot-$PID_ARG.pid"
echo "  PID: $(cat /tmp/bot-$PID_ARG.pid)"
exit 0
