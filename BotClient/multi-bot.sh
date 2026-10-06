#!/bin/bash
# ═══════════════════════════════════════════════════════════════
# 多个人机 —— 顺序启动，避免同时加入互相撞车
#
# 为什么必须顺序：
#   房主用 GameData.GetAvailableId() 分配 playerId（取最小空闲号），
#   几个人机**同时**加入时，房主可能还没处理完前一个就分配下一个，
#   于是拿到相同的 id。间隔几秒顺序加入最稳。
#
# 用法:  ./multi-bot.sh [数量] [起始颜色] [投票目标playerId]
#   第三个参数 = 内鬼的 playerId，给了就会在会议开始时投票给他
# ═══════════════════════════════════════════════════════════════
set -u
N="${1:-3}"
C0="${2:-1}"
VOTE="${3:--1}"   # -1 = 不投票
GAME=$(readlink -f "$HOME/.local/share/Steam/steamapps/common/Among Us")
LOG="$GAME/BepInEx/LogOutput.log"
DIR="$(cd "$(dirname "$0")" && pwd)"
export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$HOME/.dotnet:$PATH"; export DOTNET_CLI_TELEMETRY_OPTOUT=1

NAMES=(BotA BotB BotC BotD BotE BotF)

echo "════════ 多个人机启动 ════════"

# ── 前置检查 ──
pgrep -x "Among Us.exe" >/dev/null 2>&1 || { echo "  ❌ 游戏未运行"; exit 1; }
ss -ulpn 2>/dev/null | grep -q ':22023' || { echo "  ❌ 不在房间里"; exit 2; }
CNT=$(grep -a '玩家表' "$LOG" 2>/dev/null | tail -1 | grep -oP '玩家表\(\K[0-9]+')
if [ "${CNT:-1}" != "1" ]; then
    echo "  ⛔ 玩家表不干净（$CNT 条）—— 请重开一局再试"
    exit 3
fi
echo "  ✅ 前置检查通过"

# ── 清理残留 ──
for d in /proc/[0-9]*; do
    pid=${d#/proc/}; [ "$pid" = "$$" ] && continue
    [ -r "$d/cmdline" ] || continue
    exe=$(tr '\0' '\n' < "$d/cmdline" 2>/dev/null | head -1)
    case "$exe" in */dotnet|dotnet) ;; *) continue ;; esac
    tr '\0' ' ' < "$d/cmdline" 2>/dev/null | grep -q 'BotClient.dll' && kill -9 "$pid" 2>/dev/null
done
sleep 2

# ── 顺序启动 ──
cd "$DIR"
for i in $(seq 0 $((N-1))); do
    NAME="${NAMES[$i]:-Bot$i}"
    COLOR=$(( (C0 + i) % 12 ))
    PID_ARG=$(( i + 1 ))
    LOGF="/tmp/bot-$PID_ARG.log"
    echo
    echo "  ── [$((i+1))/$N] $NAME  颜色=$COLOR  playerId=$PID_ARG  投票目标=$VOTE ──"
    # 参数: gameId version user seconds _rpcNetId _netBase color forcePid rpcTarget voteFor
    nohup dotnet bin/Release/net6.0/BotClient.dll 32 50663600 "$NAME" 0 0 8 "$COLOR" "$PID_ARG" 0 "$VOTE" > "$LOGF" 2>&1 &
    echo "     PID $!  →  $LOGF"
    # 等这个人的角色真正被房主创建出来（收到 Spawn）再放下一个
    for t in $(seq 1 20); do
        sleep 1
        grep -q '玩家 netId=' "$LOGF" 2>/dev/null && { echo "     ✅ 已就位（$t 秒）"; break; }
    done
done

echo
echo "════════ 启动完成，等待稳定 ════════"
sleep 8
echo "  实例数: $(n=0; for d in /proc/[0-9]*; do pid=${d#/proc/}; [ "$pid" = "$$" ] && continue; [ -r "$d/cmdline" ] || continue; exe=$(tr '\0' '\n' < "$d/cmdline" 2>/dev/null | head -1); case "$exe" in */dotnet|dotnet) tr '\0' ' ' < "$d/cmdline" 2>/dev/null | grep -q 'BotClient.dll' && n=$((n+1));; esac; done; echo $n)"
echo "  房主玩家表: $(grep -a '玩家表' "$LOG" | tail -1 | grep -oP '玩家表\(\K[0-9]+') 条"
echo "  断连: $(grep -ac 'HandleDisconnect' "$LOG" 2>/dev/null) 次"
