#!/bin/bash
# ═══════════════════════════════════════════════════════════════
# 让某台人机「拍桌」（按紧急按钮）
#
# 原理：人机的 PressLoop 监听 /tmp/press-<pid>.trigger，
#       文件一出现就发 ReportDeadBody(0xFF) 给房主。
#       （源码依据：EmergencyMinigame.cs:88 CmdReportDeadBody(null)）
#
# 用法:  ./press.sh          # 默认让 BotA 拍
#        ./press.sh 2        # 让 pid=2 那台拍
#        ./press.sh all      # 三台一起拍（不推荐，会显示三票齐发）
# ═══════════════════════════════════════════════════════════════
set -u
TARGET="${1:-1}"

found=0
for f in /tmp/bot-*.pid; do
    [ -f "$f" ] || continue
    id=$(basename "$f" .pid | sed 's/bot-//')
    pid=$(cat "$f" 2>/dev/null)
    [ -z "$pid" ] && continue
    kill -0 "$pid" 2>/dev/null || continue
    if [ "$TARGET" = "all" ] || [ "$TARGET" = "$id" ]; then
        touch "/tmp/press-$pid.trigger"
        echo "  ✅ 已通知 pid=$id (进程 $pid) 拍桌"
        found=$((found+1))
    fi
done

if [ "$found" = 0 ]; then
    echo "  ❌ 没找到匹配的人机"
    echo "  当前在跑的人机:"
    for f in /tmp/bot-*.pid; do
        [ -f "$f" ] || continue
        id=$(basename "$f" .pid | sed 's/bot-//')
        pid=$(cat "$f" 2>/dev/null)
        kill -0 "$pid" 2>/dev/null && echo "     pid=$id (进程 $pid)"
    done
fi
