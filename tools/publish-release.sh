#!/usr/bin/env bash
# 把当前提交与新的 BetterWebLauncher.exe 发布到 GitHub。
#
# 用法：
#   GITHUB_TOKEN=ghp_xxx bash tools/publish-release.sh
#
# 需要的 token 权限：repo（classic）或 Contents: Read and write（fine-grained）。
# 脚本只做三件事：push 代码、换掉 Release 里的 exe、打印验证结果。

set -euo pipefail

REPO="ZYCforever/BeLuvky"
TAG="BWL-BetterWebLancher"
RELEASE_ID="409594887"
ASSET_NAME="BetterWebLauncher.exe"
EXE="bin/Release/net8.0-windows/win-x64/publish/BetterWebLauncher.exe"

: "${GITHUB_TOKEN:?请设置 GITHUB_TOKEN 环境变量后再运行}"

cd "$(dirname "$0")/.."

if [ ! -f "$EXE" ]; then
    echo "找不到 $EXE，请先发布：" >&2
    echo "  dotnet publish -c Release" >&2
    exit 1
fi

api() {
    local method="$1" url="$2"; shift 2
    curl -sS -X "$method" \
        -H "Authorization: Bearer $GITHUB_TOKEN" \
        -H "Accept: application/vnd.github+json" \
        -H "X-GitHub-Api-Version: 2022-11-28" \
        "$@" "$url"
}

echo "==> 1/4 推送代码"
# token 只放在这条命令的环境里，不写进 .git/config
git -c credential.helper= \
    push "https://x-access-token:${GITHUB_TOKEN}@github.com/${REPO}.git" HEAD:main

echo "==> 2/4 删除旧的 Release 附件"
old_ids=$(api GET "https://api.github.com/repos/${REPO}/releases/${RELEASE_ID}/assets" \
    | python3 -c 'import json,sys; print(" ".join(str(a["id"]) for a in json.load(sys.stdin)))')
for id in $old_ids; do
    echo "    - asset $id"
    api DELETE "https://api.github.com/repos/${REPO}/releases/assets/${id}" >/dev/null
done

echo "==> 3/4 上传新的 ${ASSET_NAME}（$(du -h "$EXE" | cut -f1)）"
api POST "https://uploads.github.com/repos/${REPO}/releases/${RELEASE_ID}/assets?name=${ASSET_NAME}" \
    -H "Content-Type: application/octet-stream" \
    --data-binary "@${EXE}" \
    | python3 -c 'import json,sys; d=json.load(sys.stdin); print("    uploaded:", d.get("name"), d.get("size"), "bytes", d.get("browser_download_url"))'

echo "==> 4/4 验证"
api GET "https://api.github.com/repos/${REPO}/releases/tags/${TAG}" \
    | python3 -c '
import json,sys
d = json.load(sys.stdin)
print("    release:", d["name"], "| tag:", d["tag_name"], "| draft:", d["draft"])
for a in d["assets"]:
    print("    asset  :", a["name"], a["size"], "bytes")
    print("    url    :", a["browser_download_url"])
'
echo
echo "完成：https://github.com/${REPO}/releases/tag/${TAG}"
