"""Claude skills of this repository (skills/<name>/SKILL.md):

    .venv\\Scripts\\python.exe tools\\pack_skills.py            # dist\\skills\\<name>.zip — upload in Claude Desktop
    .venv\\Scripts\\python.exe tools\\pack_skills.py --install  # + copy to %USERPROFILE%\\.claude\\skills (Claude Code)

Zip entries use forward slashes (<name>/SKILL.md); PowerShell 5.1 Compress-Archive writes backslashes."""
import os
import shutil
import sys
import zipfile

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(REPO, "skills")
DIST = os.path.join(REPO, "dist", "skills")


def main(install: bool):
    os.makedirs(DIST, exist_ok=True)
    dst_root = os.path.join(os.path.expanduser("~"), ".claude", "skills")
    for name in sorted(os.listdir(SRC)):
        folder = os.path.join(SRC, name)
        if not os.path.isfile(os.path.join(folder, "SKILL.md")):
            continue
        zp = os.path.join(DIST, name + ".zip")
        with zipfile.ZipFile(zp, "w", zipfile.ZIP_DEFLATED) as z:
            for base, _, files in os.walk(folder):
                for f in files:
                    full = os.path.join(base, f)
                    z.write(full, os.path.relpath(full, SRC).replace(os.sep, "/"))
        line = f"{name}: {zp}"
        if install:
            target = os.path.join(dst_root, name)
            shutil.rmtree(target, ignore_errors=True)
            shutil.copytree(folder, target)
            line += f"  (Claude Code: {target})"
        print(line)


if __name__ == "__main__":
    main("--install" in sys.argv)
