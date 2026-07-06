# Renders USER_GUIDE.md to the standalone USER_GUIDE.html the release ships.
# publish-release.cmd runs it at publish time, so unlike make-icon.py nothing
# generated is committed and the offline manual can never lag the markdown.
#
#   python make-user-guide.py <output.html>   (needs: pip install markdown)
#
# The markdown stays the single source of truth (GitHub renders it; the README
# links it); this produces the offline copy Help (F1) opens. HTML rather than
# the raw .md because many PCs have no .md file association, so Help landed on
# Windows' "can't open this type of file" prompt.
import re
import sys
from pathlib import Path

try:
    import markdown
except ImportError:
    sys.exit("make-user-guide.py: the 'markdown' package is missing "
             "(pip install markdown).")

REPO_URL = "https://github.com/PlanetLinux98/guard"

# Embedded stylesheet so the file is fully self-contained offline. Follows the
# OS light/dark preference (prefers-color-scheme) like the app's Mica theming;
# colours in both schemes keep WCAG AA contrast against their background.
TEMPLATE = """<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light dark">
<title>GUARD User Manual</title>
<style>
:root {{
  color-scheme: light dark;
  --bg: #ffffff; --fg: #1a1a1a; --link: #0b57d0;
  --border: #c9c9c9; --code-bg: #f2f2f2; --quote: #575757;
}}
@media (prefers-color-scheme: dark) {{
  :root {{
    --bg: #1e1e1e; --fg: #e6e6e6; --link: #8ab4f8;
    --border: #4a4a4a; --code-bg: #2a2a2a; --quote: #b8b8b8;
  }}
}}
body {{
  background: var(--bg); color: var(--fg);
  font-family: "Segoe UI", system-ui, sans-serif;
  font-size: 1.0625rem; line-height: 1.6;
  max-width: 46rem; margin: 0 auto; padding: 1.5rem;
}}
a {{ color: var(--link); }}
:focus-visible {{ outline: 3px solid var(--link); outline-offset: 2px; }}
h1, h2, h3 {{ line-height: 1.25; }}
h2 {{ border-bottom: 1px solid var(--border); padding-bottom: .25rem; margin-top: 2.5rem; }}
code {{ background: var(--code-bg); padding: .1em .3em; border-radius: 4px; }}
pre {{ background: var(--code-bg); padding: .75rem 1rem; border-radius: 6px; overflow-x: auto; }}
pre code {{ background: none; padding: 0; }}
code, pre {{ font-family: Consolas, "Cascadia Mono", monospace; font-size: .95em; }}
blockquote {{
  border-left: 4px solid var(--border); color: var(--quote);
  margin: 1rem 0; padding: .25rem 1rem;
}}
table {{ border-collapse: collapse; width: 100%; }}
th, td {{ border: 1px solid var(--border); padding: .4rem .6rem; text-align: left; vertical-align: top; }}
th {{ background: var(--code-bg); }}
hr {{ border: none; border-top: 1px solid var(--border); margin: 2rem 0; }}
</style>
</head>
<body>
<main>
{body}
</main>
</body>
</html>
"""


def rewrite_relative_links(html: str) -> str:
    # Repo-relative links (README.md, LICENSE) have no target in the shipped
    # folder; point them at the repo on GitHub instead.
    def repl(m: re.Match) -> str:
        href = m.group(2)
        if href.startswith(("#", "http://", "https://", "mailto:")):
            return m.group(0)
        return f'{m.group(1)}{REPO_URL}/blob/main/{href}"'
    return re.sub(r'(href=")([^"]+)"', repl, html)


def lint_anchors(html: str, source: Path) -> None:
    ids = set(re.findall(r'id="([^"]+)"', html))
    broken = [a for a in re.findall(r'href="#([^"]+)"', html) if a not in ids]
    if broken:
        sys.exit(f"make-user-guide.py: broken internal anchors in {source}: "
                 + ", ".join(sorted(set(broken))))


def main() -> None:
    if len(sys.argv) != 2:
        sys.exit("usage: make-user-guide.py <output.html>")
    source = Path(__file__).resolve().parent / "USER_GUIDE.md"
    out = Path(sys.argv[1])

    text = source.read_text(encoding="utf-8")
    # toc is only for its heading ids (its default slugs match GitHub's for
    # these headings, keeping the manual's #anchors working in both renders).
    body = markdown.markdown(
        text, extensions=["toc", "tables", "fenced_code"])
    body = rewrite_relative_links(body)
    lint_anchors(body, source)

    out.write_text(TEMPLATE.format(body=body), encoding="utf-8", newline="\n")
    print(f"Rendered {source.name} -> {out}")


if __name__ == "__main__":
    main()
