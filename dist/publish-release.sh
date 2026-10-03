#!/bin/bash
# 发布 SmartLocal 到 GitHub Releases
#
# 用法（token 只存在于环境变量，不写入磁盘、不进命令参数）:
#   GH_TOKEN=ghp_xxx ~/AmongUsMods/dist/publish-release.sh
#
# 需要 token 具备该仓库的 Contents: Read and write 权限。

set -euo pipefail

REPO="bcdidit67/AmongUs-AdvancedBot"
TAG="v0.4.0-hivemind"
TITLE="v0.4.0-hivemind — 假人注入全链路打通（技术里程碑）"
ASSET="$HOME/AmongUsMods/dist/SmartLocal-v0.4.0-hivemind.zip"
NOTES="$HOME/AmongUsMods/dist/RELEASE_NOTES.md"

: "${GH_TOKEN:?请设置 GH_TOKEN 环境变量，例如: GH_TOKEN=ghp_xxx $0}"

[ -f "$ASSET" ] || { echo "❌ 找不到发布包: $ASSET"; exit 1; }
[ -f "$NOTES" ] || { echo "❌ 找不到发布说明: $NOTES"; exit 1; }

# 把 Authorization 头写进 600 权限的 curl 配置文件，用 -K 读取 ——
# 这样 token 不会出现在任何进程的 argv 里（ps 看不到）。
CURL_CONF=$(mktemp)
chmod 600 "$CURL_CONF"
trap 'rm -f "$CURL_CONF"' EXIT
{
  printf 'header = "Authorization: token %s"\n' "$GH_TOKEN"
  printf 'header = "Accept: application/vnd.github+json"\n'
} > "$CURL_CONF"

AUTH=(-K "$CURL_CONF")

echo "== 0/2  验证 token =="
curl -sS "${AUTH[@]}" "https://api.github.com/repos/$REPO" \
| python3 -c '
import json, sys
d = json.load(sys.stdin)
if "full_name" not in d:
    sys.stderr.write("❌ token 无效或无权访问: %s\n" % d.get("message", d)); sys.exit(1)
perms = d.get("permissions") or {}
if not perms.get("push"):
    sys.stderr.write("❌ token 缺少 push 权限\n"); sys.exit(1)
print("     ok  %s (push=%s)" % (d["full_name"], perms.get("push")))
'

echo "== 1/2  创建 Release（tag: $TAG）=="
BODY_JSON=$(python3 - "$TAG" "$TITLE" "$NOTES" <<'PY'
import json, sys
tag, title, notes_path = sys.argv[1], sys.argv[2], sys.argv[3]
body = open(notes_path, encoding='utf-8').read()
print(json.dumps({
    "tag_name": tag,
    "name": title,
    "body": body,
    "prerelease": True,
    "draft": False,
}, ensure_ascii=False))
PY
)

RESP=$(curl -sS -X POST "${AUTH[@]}" \
        "https://api.github.com/repos/$REPO/releases" \
        -d "$BODY_JSON")

UPLOAD_URL=$(printf '%s' "$RESP" | python3 -c '
import json, sys
d = json.load(sys.stdin)
if "upload_url" not in d:
    sys.stderr.write("❌ 创建 Release 失败: %s\n" % d.get("message", d))
    sys.exit(1)
print(d["upload_url"].split("{")[0])
') || { printf '%s\n' "$RESP" | head -c 500; exit 1; }

echo "     ok"

echo "== 2/2  上传资源 $(basename "$ASSET") =="
curl -sS -X POST "${AUTH[@]}" \
     -H "Content-Type: application/zip" \
     --data-binary "@$ASSET" \
     "$UPLOAD_URL?name=$(basename "$ASSET")" \
| python3 -c '
import json, sys
d = json.load(sys.stdin)
if "browser_download_url" not in d:
    sys.stderr.write("❌ 上传失败: %s\n" % d.get("message", d)); sys.exit(1)
print("     资源: %s  (%s 字节)" % (d["name"], d["size"]))
print("     下载: %s" % d["browser_download_url"])
'

echo
echo "✅ 完成: https://github.com/$REPO/releases/tag/$TAG"
