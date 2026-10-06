#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Among Us 人机客户端 —— 跨平台 TUI 启动器
================================================

零依赖（只用标准库），Linux / Windows 通用。

  python tui.py

它负责：
  · 自动检测游戏目录、日志、插件是否就位
  · 一键启动 N 台人机（顺序加入，避免 playerId 撞车）
  · 实时看状态（哪几台活着、拿到什么 netId）
  · 让某台「拍桌」（按紧急按钮）或「刀人」

为什么用 Python 而不是 shell：
  shell 脚本在 Windows 上跑不了（除非装 Git Bash），
  而这份 TUI 两个平台都能直接双击/命令行运行。
"""

import os
import re
import sys
import glob
import time
import signal
import shutil
import subprocess

# ─────────────────────────────────────────────────────────────
# 平台与路径
# ─────────────────────────────────────────────────────────────
IS_WIN = sys.platform.startswith("win")
HERE = os.path.dirname(os.path.abspath(__file__))

# BotClient.dll 的位置随「打包布局 / 源码布局」而不同，逐个找：
#   · <包根>/bot/BotClient.dll                    （解压即用的包）
#    · <源码>/bin/Release/net6.0/BotClient.dll    （开发目录）
#    · 与本文件同级
_CANDS = [
    os.path.join(HERE, "bot", "BotClient.dll"),
    os.path.join(HERE, "bin", "Release", "net6.0", "BotClient.dll"),
    os.path.join(HERE, "bin", "Debug", "net6.0", "BotClient.dll"),
    os.path.join(HERE, "BotClient.dll"),
]
BOT_DLL = next((p for p in _CANDS if os.path.isfile(p)), _CANDS[0])
BOT_DIR = os.path.dirname(BOT_DLL)

GAME_ID = 32
VERSION = 50663600
TMP = os.environ.get("TEMP", "/tmp") if IS_WIN else "/tmp"


def find_game_dir():
    """找 Among Us 安装目录。可用 AMONGUS_DIR 环境变量覆盖。"""
    env = os.environ.get("AMONGUS_DIR")
    if env and os.path.isdir(env):
        return env

    cands = []
    if IS_WIN:
        for base in (r"C:\Program Files (x86)\Steam\steamapps\common",
                     r"C:\Program Files\Steam\steamapps\common",
                     r"D:\Steam\steamapps\common",
                     r"E:\Steam\steamapps\common"):
            cands.append(os.path.join(base, "Among Us"))
    else:
        home = os.path.expanduser("~")
        cands += [
            os.path.join(home, ".local/share/Steam/steamapps/common/Among Us"),
            os.path.join(home, ".steam/steam/steamapps/common/Among Us"),
            "/usr/share/steam/steamapps/common/Among Us",
        ]
    for c in cands:
        if os.path.isdir(c):
            return c
    return None


GAME_DIR = find_game_dir()
LOG_PATH = os.path.join(GAME_DIR, "BepInEx", "LogOutput.log") if GAME_DIR else None
PLUGIN_DIR = os.path.join(GAME_DIR, "BepInEx", "plugins") if GAME_DIR else None


# ─────────────────────────────────────────────────────────────
# ANSI 小工具（Windows 10+ 也支持）
# ─────────────────────────────────────────────────────────────
if IS_WIN:
    os.system("")          # 让 Windows 终端开启 ANSI 转义

C = {
    "r": "\033[0m", "b": "\033[1m", "dim": "\033[2m",
    "red": "\033[31m", "grn": "\033[32m", "yel": "\033[33m",
    "blu": "\033[34m", "mag": "\033[35m", "cyn": "\033[36m",
}
W = 62


def col(s, c):
    return f"{C.get(c,'')}{s}{C['r']}"


def line(ch="─"):
    print("  " + ch * W)


def title(s):
    print()
    print(col("  ┌" + "─" * W + "┐", "cyn"))
    pad = W - len(s) - 2
    print(col("  │ ", "cyn") + col(s, "b") + " " * max(0, pad) + col(" │", "cyn"))
    print(col("  └" + "─" * W + "┘", "cyn"))


def ok(s):    print(f"    {col('✅', 'grn')} {s}")
def bad(s):   print(f"    {col('❌', 'red')} {s}")
def warn(s):  print(f"    {col('⚠️ ', 'yel')} {s}")
def info(s):  print(f"    {col('·', 'dim')} {s}")


# ─────────────────────────────────────────────────────────────
# 进程管理
# ─────────────────────────────────────────────────────────────
def list_bots():
    """返回在跑的人机列表 [(name, playerId, pid), ...]"""
    out = []
    for d in glob.glob("/proc/[0-9]*" if not IS_WIN else ""):
        pid = os.path.basename(d)
        try:
            with open(os.path.join(d, "cmdline"), "rb") as f:
                parts = f.read().split(b"\0")
            if not parts or b"BotClient.dll" not in b" ".join(parts):
                continue
            exe = parts[0].decode(errors="replace")
            if "dotnet" not in exe:
                continue
            # 参数顺序（Program.cs 的 Main）：
            #   0=dotnet 1=dll 2=gameId 3=version 4=user 5=seconds
            #   6=_rpcNetId 7=_netBase 8=color 9=forcePid 10=rpcTarget 11=voteFor
            name = parts[4].decode(errors="replace") if len(parts) > 4 else "?"
            pidarg = parts[9].decode(errors="replace") if len(parts) > 9 else "?"
            out.append((name, pidarg, int(pid)))
        except Exception:
            continue

    # ★ /proc 扫描会对同一台人机产生重复条目（dotnet 的进程结构），去重
    dedup = {}
    for name, pidarg, pid in out:
        dedup[pid] = (name, pidarg, pid)
    out = list(dedup.values())

    if IS_WIN:
        try:
            r = subprocess.run(["wmic", "process", "where",
                                "name='dotnet.exe'", "get", "ProcessId,CommandLine"],
                               capture_output=True, text=True, timeout=10)
            for ln in r.stdout.splitlines():
                if "BotClient.dll" not in ln:
                    continue
                m = re.search(r"BotClient\.dll\s+(\d+)\s+(\d+)\s+(\S+)\s+\S+\s+\S+\s+\S+\s+\S+\s+(\d+)", ln)
                if m:
                    out.append((m.group(3), m.group(4), int(m.group(2))))
        except Exception:
            pass
    return out


def kill_all():
    n = 0
    for _, _, pid in list_bots():
        try:
            if IS_WIN:
                subprocess.run(["taskkill", "/F", "/PID", str(pid)],
                               capture_output=True, timeout=10)
            else:
                os.kill(pid, signal.SIGKILL)
            n += 1
        except Exception:
            pass
    return n


# ─────────────────────────────────────────────────────────────
# 环境检测
# ─────────────────────────────────────────────────────────────
def game_running():
    if IS_WIN:
        try:
            r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq Among Us.exe"],
                               capture_output=True, text=True, timeout=10)
            return "Among Us.exe" in r.stdout
        except Exception:
            return False
    for d in glob.glob("/proc/[0-9]*"):
        try:
            with open(os.path.join(d, "comm")) as f:
                if f.read().strip() == "Among Us.exe":
                    return True
        except Exception:
            continue
    return False


def port_listening():
    """22023 是否在监听 —— 等价于「人在本地大厅里」"""
    try:
        if IS_WIN:
            r = subprocess.run(["netstat", "-an"], capture_output=True, text=True, timeout=10)
            return ":22023" in r.stdout
        r = subprocess.run(["ss", "-ulpn"], capture_output=True, text=True, timeout=10)
        return ":22023" in r.stdout
    except Exception:
        return False


def plugin_installed():
    return bool(PLUGIN_DIR) and os.path.isfile(os.path.join(PLUGIN_DIR, "ProtoDump.dll"))


def player_table():
    """从插件日志里读最后一条玩家表（对局结束后会过期，仅供参考）"""
    if not LOG_PATH or not os.path.isfile(LOG_PATH):
        return None
    try:
        with open(LOG_PATH, errors="replace") as f:
            rows = [l for l in f if "玩家表" in l]
        if not rows:
            return None
        m = re.search(r"玩家表\((\d+)\):(.*)", rows[-1])
        return (int(m.group(1)), m.group(2).strip()) if m else None
    except Exception:
        return None


def bot_state(i):
    """读某台人机的日志，返回 (netId 或 None, 位置包数)"""
    p = os.path.join(TMP, f"bot-{i}.log")
    if not os.path.isfile(p):
        return (None, 0)
    try:
        txt = open(p, errors="replace").read()
        m = re.findall(r"玩家 netId=(\d+)", txt)
        return (int(m[-1]) if m else None, txt.count("位置("))
    except Exception:
        return (None, 0)


# ─────────────────────────────────────────────────────────────
# 主界面
# ─────────────────────────────────────────────────────────────
def show_status():
    title("环境")
    if GAME_DIR:
        ok(f"游戏目录  {col(GAME_DIR, 'dim')}")
    else:
        bad("没找到游戏目录 —— 请设置环境变量 AMONGUS_DIR 指向 Among Us 文件夹")
    ok("游戏进程  运行中") if game_running() else bad("游戏进程  未运行")
    ok("端口 22023 监听中（在本地大厅里）") if port_listening() else \
        warn("端口 22023 未监听 —— 请先在游戏里「本地创建游戏」")
    ok("插件已安装  BepInEx/plugins/ProtoDump.dll") if plugin_installed() else \
        warn("插件未安装 —— 把 plugin/ProtoDump.dll 放进 BepInEx/plugins/")

    t = player_table()
    if t:
        info(f"日志里的玩家表：{t[0]} 条{col('（对局结束后会过期，以画面为准）','dim')}")

    title("人机")
    bots = list_bots()
    if not bots:
        info("当前没有在跑的人机")
    else:
        ok(f"在跑 {len(bots)} 台")
        for name, pidarg, pid in sorted(bots, key=lambda x: int(x[1]) if x[1].isdigit() else 0):
            nid, pkts = bot_state(pidarg)
            nid_s = f"netId={nid}" if nid else col("未就位", "yel")
            print(f"      {col(name,'b'):<14} pid={pidarg:<3} {nid_s:<16} 位置包 {pkts}")


def do_start():
    try:
        n = int(input("    要启动几台人机? [14] ").strip() or "14")
        c0 = int(input("    起始颜色 (0=红 1=蓝 2=绿 …) [1] ").strip() or "1")
        vf = int(input("    投票目标 playerId (-1 = 不投票) [-1] ").strip() or "-1")
    except (ValueError, EOFError):
        bad("输入无效")
        return

    if not GAME_DIR or not os.path.isfile(BOT_DLL):
        bad("游戏目录或 BotClient.dll 找不到，无法启动")
        return

    kill_all()
    time.sleep(1)

    names = [f"Bot{i+1:02d}" for i in range(n)]
    env = dict(os.environ)
    env["AMONGUS_DIR"] = GAME_DIR
    dotnet = shutil.which("dotnet")
    if not dotnet:
        bad("找不到 dotnet —— 请先安装 .NET 6 运行时")
        return

    print()
    for i in range(n):
        name = names[i]
        color = (c0 + i) % 12
        pidarg = i + 1
        logf = os.path.join(TMP, f"bot-{pidarg}.log")
        args = [dotnet, BOT_DLL, str(GAME_ID), str(VERSION), name, "0", "0", "8",
                str(color), str(pidarg), "0", str(vf)]
        try:
            with open(logf, "wb") as lf:
                p = subprocess.Popen(args, stdout=lf, stderr=subprocess.STDOUT,
                                     env=env, cwd=BOT_DIR,
                                     creationflags=subprocess.CREATE_NO_WINDOW if IS_WIN else 0)
            # 记下 pid，方便后面拍桌/刀人（跨平台下用日志文件传递）
            with open(os.path.join(TMP, f"bot-{pidarg}.pid"), "w") as f:
                f.write(str(p.pid))
        except Exception as e:
            bad(f"{name} 启动失败: {e}")
            continue

        # 等它真正拿到角色（收到房主的 Spawn）再放下一个 ——
        # 房主用 GameData.GetAvailableId() 分配 playerId，
        # 同时加入会拿到相同 id。
        ready = False
        for _ in range(120):
            time.sleep(0.5)
            nid, _ = bot_state(pidarg)
            if nid:
                ready = True
                break
        print(f"    [{i+1:>2}/{n}] {name:<7} 颜色={color:<3} "
              + (col("✅ 已就位", "grn") if ready else col("… 超时未确认", "yel")))
    print()


def do_press():
    bots = list_bots()
    if not bots:
        bad("没有在跑的人机")
        return
    who = input(f"    让哪台拍桌? 输入 playerId [1] ").strip() or "1"
    for name, pidarg, pid in bots:
        if pidarg == who:
            open(os.path.join(TMP, f"press-{pid}.trigger"), "w").close()
            ok(f"已通知 {name} 拍桌")
            return
    bad(f"没找到 playerId={who} 的人机")


def do_kill():
    bots = list_bots()
    if not bots:
        bad("没有在跑的人机")
        return
    # 从日志里列出已知目标
    print("    已知玩家对象（clientId / netId）：")
    seen = set()
    for i in range(1, 20):
        p = os.path.join(TMP, f"bot-{i}.log")
        if not os.path.isfile(p):
            continue
        try:
            for m in re.finditer(r"玩家 Spawn: clientId=(\d+) PlayerControl netId=(\d+)",
                                 open(p, errors="replace").read()):
                seen.add((int(m.group(1)), int(m.group(2))))
        except Exception:
            pass
    for cid, nid in sorted(seen):
        info(f"clientId={cid}  netId={nid}")

    who = input("    让哪台去刀? 输入它的 playerId [1] ").strip() or "1"
    tgt = input("    刀谁? 输入目标的 netId ").strip()
    if not tgt.isdigit():
        bad("目标 netId 无效")
        return
    for name, pidarg, pid in bots:
        if pidarg == who:
            with open(os.path.join(TMP, f"kill-{pid}.trigger"), "w") as f:
                f.write(tgt)
            ok(f"已通知 {name} 去刀 netId={tgt}")
            return
    bad(f"没找到 playerId={who} 的人机")


def main():
    while True:
        os.system("cls" if IS_WIN else "clear")
        print()
        print(col("  ╔" + "═" * W + "╗", "mag"))
        s = "Among Us 人机客户端  ·  跨平台启动器"
        print(col("  ║ ", "mag") + col(s, "b") + " " * (W - len(s) - 1) + col("║", "mag"))
        print(col("  ╚" + "═" * W + "╝", "mag"))

        show_status()

        title("操作")
        print("    " + col("[1]", "cyn") + " 启动人机     "
              + col("[2]", "cyn") + " 全部停止")
        print("    " + col("[3]", "cyn") + " 让某台拍桌   "
              + col("[4]", "cyn") + " 让某台刀人")
        print("    " + col("[5]", "cyn") + " 刷新状态     "
              + col("[0]", "cyn") + " 退出")
        print()
        try:
            c = input("    选择 > ").strip()
        except (EOFError, KeyboardInterrupt):
            print()
            break

        if c == "1":
            do_start()
        elif c == "2":
            n = kill_all()
            ok(f"已停止 {n} 台")
        elif c == "3":
            do_press()
        elif c == "4":
            do_kill()
        elif c == "5":
            continue
        elif c == "0":
            break
        else:
            warn("无效选项")

        print()
        try:
            input(col("    回车继续…", "dim"))
        except (EOFError, KeyboardInterrupt):
            break


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print()
