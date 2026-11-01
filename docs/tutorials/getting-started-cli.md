# Get started with the ferret command

In this tutorial you will install `ferret`, run a plain search, then run your first ranked search. It takes about ten minutes. You need the .NET SDK and a folder of markdown or text files.

## 1. Install

```sh
dotnet tool install --global Ferret.Tool
ferret --version
```

You should see a version number.

## 2. Make some files to search

```sh
mkdir ferret-demo && cd ferret-demo
echo "Retry failed AWS calls with exponential backoff." > retries.md
echo "Mock the clock so unit tests do not depend on time." > testing.md
echo "Install the tool with dotnet tool install." > install.md
```

## 3. Run a plain search

```sh
ferret -n -i retry
```

Output:

```
retries.md:1:Retry failed AWS calls with exponential backoff.
```

This is the same as `rg -n -i retry`. Plain searches are case-sensitive, like ripgrep, so without `-i` the pattern `retry` would not match "Retry". Plain searches show every match, in file order, with no ranking.

## 4. Run a ranked search

```sh
ferret --lex "how to mock time in tests" --scores
```

Output (scores will vary):

```
[2.8941] testing.md:1:Mock the clock so unit tests do not depend on time.
```

`--lex` treats your words as search terms, not a regex, and ranks files with BM25. The best file comes first. Stop words such as "how" and "to" are ignored.

## 5. Search by meaning

```sh
ferret --sem "what do I do when a request fails" --scores
```

The first run downloads a small model (about 32 MB), then indexes your files. `retries.md` should rank first even though it shares no words with your question.

Output (scores will vary):

```
[0.6444] retries.md:1:Retry failed AWS calls with exponential backoff.
[0.4652] testing.md:1:Mock the clock so unit tests do not depend on time.
[0.3905] install.md:1:Install the tool with dotnet tool install.
```

Semantic search returns every file, best first, so lower results are weak matches. While it indexes, `ferret` prints progress such as `embedding 3 chunks` to stderr.

## 6. Combine both

```sh
ferret --hybrid "retry aws" --scores
```

Output (scores will vary):

```
[0.0328] retries.md:1:Retry failed AWS calls with exponential backoff.
[0.0161] testing.md:1:Mock the clock so unit tests do not depend on time.
[0.0159] install.md:1:Install the tool with dotnet tool install.
```

Hybrid fuses the lexical and semantic rankings. The scores are small (about 0.03 at most) because they are rank-fusion scores, not BM25 scores.

## What you learned

You installed Ferret, ran a ripgrep-style search, and ran lexical, semantic and hybrid ranked searches.

Next:

- Task recipes: [How-to guides](../index.md#how-to-guides)
- All flags: [Command line reference](../reference/cli.md)
- Why there are three modes: [Search modes](../explanation/search-modes.md)
