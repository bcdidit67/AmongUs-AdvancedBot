#!/bin/bash
# ═══════════════════════════════════════════════════════════════
# 让某台人机「刀人」
#
# 源码依据：PlayerControl.RpcMurderPlayer(target)
#   StartRpcImmediately(this.NetId, 12, Reliable, -1)
#   WriteNetObject(target)              ← 目标的 netId
# 房主侧会校验 Data.IsImpostor —— 不是内鬼就刀不动（原版逻辑）。
#
# 用法:
#   ./kill.sh              # 列出所有已知目标（clientId + netId）
#   ./kill.sh 1 37         # 让 pid=1 那台去刀 netId=37 的目标
#   ./kill.sh 1 37 kill    # 同上（显式写 kill）
# ═══════════════════════════════════════════════════════════════
set -u

list_targets() {
    echo "  ── 已知的玩家对象（从人机日志里解析）──"
    for f in /tmp/bot-*.log; do
        [ -f "$f" ] || continue
        grep -oE '· 玩家 Spawn: clientId=[0-9]+ PlayerControl netId=[0-9]+' "$f" 2>/dev/null
    done | sort -u | sed 's/· 玩家 Spawn: //' | awk '{print "    " $1 "  " $2}'
    echo
    echo "  ── 在跑的人机 ──"
    for f in /tmp/bot-*.pid; do
        [ -f "$f" ] || continue
        id=$(basename "$f" .pid | sed 's/bot-//')
        pid=$(cat "$f" 2>/dev/null)
        kill -0 "$pid" 2>/dev/null && echo "    pid=$id  (进程 $pid, 触发文件 /tmp/kill-$pid.trigger)"
    done
}

if [ $# -lt 2 ]; then
    list_targets
    echo
    echo "  用法: ./kill.sh <人机playerId> <目标netId>"
    exit 0
fi

WHO="$1"; TARGET="$2"
pid=$(cat "/tmp/bot-$WHO.pid" 2>/dev/null)
if [ -z "$pid" ] || ! kill -0 "$pid" 2>/dev/null; then
    echo "  ❌ 没找到 pid=$WHO 的人机"; list_targets; exit 1
fi
echo "$TARGET" > "/tmp/kill-$pid.trigger"
echo "  ✅ 已通知 pid=$WHO (进程 $pid) 去刀 netId=$TARGET"
