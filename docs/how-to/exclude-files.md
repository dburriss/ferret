# Exclude files from a search

Ferret skips hidden files, symlinks and binary files, and follows ignore files. To exclude more:

1. Create a `.ferretignore` in the directory to cover (or any parent within the search root).
2. Add gitignore-syntax patterns:

   ```
   generated/
   *.min.js
   !generated/keep.md
   ```

`.ferretignore` applies to Ferret only. To share rules with other tools use `.ignore`, or `.gitignore` inside a git repository. Within a directory `.ferretignore` overrides `.ignore`, which overrides `.gitignore`, and deeper directories win.

Ignore files are read from the search root downwards. One in a directory above the search root is not applied: `ferret foo subdir/` ignores the `.ferretignore` in the directory that contains `subdir`. Search from that directory instead, or put a `.ferretignore` inside `subdir`.

Ignored files are dropped before ranking, so they do not affect scores.

Not available yet: `--exclude`, `--no-ignore`, ignore files above the search root, global gitignore and `.git/info/exclude`.
