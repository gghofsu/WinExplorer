#!/usr/bin/env python3
"""Parse every C# file with tree-sitter and report syntax errors.

This is a *syntax* check only (it does not replace `dotnet build`), but it catches
unbalanced braces, broken generics and similar typos in environments without the
.NET SDK.
"""
from __future__ import annotations

import pathlib
import sys

from tree_sitter import Language, Parser
import tree_sitter_c_sharp as ts_csharp

ROOT = pathlib.Path(__file__).resolve().parent.parent


def main() -> int:
    parser = Parser(Language(ts_csharp.language()))
    failures = 0

    for path in sorted(ROOT.rglob("*.cs")):
        if any(part in {"bin", "obj", ".git"} for part in path.parts):
            continue

        source = path.read_bytes()
        tree = parser.parse(source)
        if not tree.root_node.has_error:
            continue

        failures += 1
        print(f"\n{path.relative_to(ROOT)}")
        for node in _error_nodes(tree.root_node):
            line, column = node.start_point
            snippet = source.splitlines()[line][:120].decode(errors="replace")
            print(f"  line {line + 1}:{column + 1}  {node.type}: {snippet}")

    if failures:
        print(f"\n{failures} file(s) with syntax errors")
        return 1

    print("All C# files parse cleanly.")
    return 0


def _error_nodes(root):
    stack = [root]
    while stack:
        node = stack.pop()
        if node.type == "ERROR" or node.is_missing:
            yield node
        if node.has_error:
            stack.extend(node.children)


if __name__ == "__main__":
    sys.exit(main())
