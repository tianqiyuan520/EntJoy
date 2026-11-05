from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]


def git(*args):
    return subprocess.check_output(['git', '-C', str(root), *args])


trees = set(git('log', '--all', '--format=%T').splitlines())
for tree in sorted(trees):
    paths = git('ls-tree', '-r', '--name-only', '-z', tree.decode()).split(b'\0')
    private = [path for path in paths
               if (path.lower() == b'docs' or path.lower().startswith(b'docs/'))
               and not path.lower().startswith(b'docs/public/')]
    if private:
        names = ', '.join(path.decode('utf-8', 'replace') for path in private)
        raise SystemExit(f'Non-public docs in historical tree {tree.decode()}: {names}')
print(f'PASS: {len(trees)} reachable historical trees contain only docs/public paths.')
