#!/bin/bash
# PreToolUse (Write|Edit): on any .cs file, re-inject the CLAUDE.md prime directive into the model's
# context AT THE MOMENT OF EDITING — session-start rules don't survive hours of debugging flow.
f=$(jq -r '.tool_input.file_path // empty')
case "$f" in
  *.cs) printf '%s' '{"hookSpecificOutput":{"hookEventName":"PreToolUse","additionalContext":"PRIME DIRECTIVE (CLAUDE.md): before this edit adds any method or logic, grep Interfaces/ Capabilities/ Routines/ for an existing implementation (tier selectors, routines, shared helpers). Reuse or EXTEND it — never hand-roll a parallel copy. All movement via INavigation. If you did not grep first, do it before proceeding."}}' ;;
esac
exit 0
