# Testing Patterns

**Analysis Date:** 2026-05-28

## Summary

**There is no automated test suite in this repository.** Both `AIBWindows/` (.NET 8 WPF) and `AIBLinux/` (Python PyQt6) ship without any unit, integration, or end-to-end tests, without any test runner, without coverage tooling, and without CI. This is a significant quality gap that should be flagged in `CONCERNS.md` (concerns focus). Details below document what is and what is missing.

## Test Framework

**Runner:** None.

Evidence:
- No `xUnit`, `NUnit`, `MSTest`, or `FluentAssertions` package reference in `AIBWindows/AIB.csproj`. The full dependency list is 16 packages (`AIBWindows/AIB.csproj:15-33`) — none of them is a test framework.
- No `.Tests.csproj`, no `.Test.csproj`, no companion test project anywhere in the tree (Glob `**/*Test*.csproj` returns zero matches).
- No `pytest`, `unittest`, `nose`, or `hypothesis` in `AIBLinux/requirements.txt` (only `PyQt6`, `pynput`, `openai`, `mss`, `Pillow`, `python-dotenv` — `AIBLinux/requirements.txt:1-6`).
- No `pytest.ini`, no `pyproject.toml`, no `setup.cfg`, no `tox.ini`, no `conftest.py`. All Glob searches returned zero matches.

**Assertion Library:** Not applicable.

**Run Commands:** Not applicable.

## Test File Organization

**Location:** No tests on disk. Glob searches for `**/*test*`, `**/*Test*`, `**/*spec*`, `**/__tests__/**`, `**/tests/**` returned zero matches outside of NuGet-generated MSBuild artifacts under `AIBWindows/obj/` (which are temp project specs, not test files).

**Naming:** Not applicable.

**Structure:** Not applicable.

## Test Structure

**Suite Organization:** Not applicable.

**Patterns:** Not applicable.

## Mocking

**Framework:** None.

**Patterns:** Not applicable.

**What to Mock / What NOT to Mock:** No project policy exists.

## Fixtures and Factories

**Test Data:** None.

**Location:** None.

## Coverage

**Requirements:** None enforced. `.gitignore:13-17` lists `htmlcov/`, `.coverage`, and `.pytest_cache/` — common Python coverage artifacts — which suggests *intent* to one day run `pytest --cov`, but no such config or invocation exists today.

**View Coverage:** Not applicable.

## Test Types

**Unit Tests:** None.

**Integration Tests:** None.

**E2E Tests:** None.

## Manual Verification Scripts (only test-like artifact in the repo)

### `AIBLinux/verify_ollama.py`

This is the only file in the repo that performs runtime verification. It is a manual smoke script, not a real test (no assertions raise; no exit code reflects failure; output is `print()`-based). It is also not invoked from CI or `run.sh`.

```python
# AIBLinux/verify_ollama.py:12-27
try:
    from app.openai_client import OpenAIClient
    client = OpenAIClient()

    print(f"Client initialized with model: {client.model}")
    print(f"Base URL in client: {client.client.base_url}")

    if str(client.client.base_url) == "http://localhost:11434/v1/":
        print("SUCCESS: Python client base URL correctly set.")
    else:
        print(f"FAILURE: Python client base URL is {client.client.base_url}")
    ...
```

What it actually does (`AIBLinux/verify_ollama.py:1-31`):
1. Stuffs the environment with dummy values (`OPENAI_API_KEY=dummy_key`, `URL=http://localhost:11434/v1`, `MODEL=llama3.2`).
2. Instantiates `OpenAIClient` from `AIBLinux/app/openai_client.py:13`.
3. String-compares `client.client.base_url` and `client.model` to expected values.
4. Prints `SUCCESS:` / `FAILURE:` lines.

What it does **not** do:
- It does not call any actual Ollama endpoint (despite the filename `verify_ollama.py`).
- It does not exercise streaming, history, image-payload paths, or any of `app/chat_window.py`.
- It does not fail the process — a `FAILURE:` line still exits 0.

This is the only test-like artifact. There is no equivalent for the WPF app.

### Other ad-hoc verification

- `AIBLinux/build.sh:1-21` — PyInstaller build script. No test invocation before packaging.
- `AIBLinux/build_universal.sh`, `AIBLinux/docker_build.sh`, `AIBLinux/run.sh` — bash conveniences, no test step.
- `AIBWindows/README.md:30-33` documents `dotnet build` / `dotnet run` only; no `dotnet test` mentioned.
- `GRAVITY.MD:58-59` mentions `Stop-Process -Name AIB -Force` as the build hygiene step before recompiling — this is the only build/run loop instruction and is manual.

## CI / CD

**No CI configured.** Searches for `.github/workflows/*`, `.gitlab-ci.yml`, `azure-pipelines.yml`, `Jenkinsfile`, and `bitbucket-pipelines.yml` returned zero matches. The repository has no automated build, no automated test, no automated lint or format check, and no release pipeline.

## What Is Implicitly Verified

Because there is no test suite, the only signal that the agent behaves correctly comes from:

1. **The `[WARMUP]` self-check loop** (`AIBWindows/Services/OpenAIService.cs:68-141`) — on each startup, the app forces Ollama to lock the model in VRAM, then sends a `[SYSTEM_HEARTBEAT]` user message expecting the literal response `"SISTEMA ONLINE"`. The result is logged but never asserted (`OpenAIService.cs:122`); a wrong answer doesn't fail anything.
2. **Verbose stream logging** (`AIBWindows/Services/OpenAIService.cs:255-302`) — gated by the `VerboseConsoleLogging` setting (`Services/SettingsService.cs:27`). When on, `[STREAM-DBG]` / `[STREAM-END]` lines surface counts of update chunks, tool updates, and content fragments. This is the developer's primary diagnostic instead of unit tests.
3. **Tool-level `try/catch`** (`AIBWindows/Services/ToolRegistry.cs:40-48`, every `ITool.ExecuteAsync` in `Services/NativeTools.cs`) — guarantees the ReAct loop receives a string instead of an exception, hiding many runtime bugs from the user but logging them via `Console.WriteLine($"[REGISTRY] ERRO em '{toolName}': {ex.Message}")` (`ToolRegistry.cs:46`).
4. **DPAPI fallback in `SettingsService.LoadSettings`** (`AIBWindows/Services/SettingsService.cs:60-79`) — falls back to plaintext JSON if decryption fails (legacy migration path). Side effect: silent corruption is recoverable; corruption diagnostics are absent.
5. **`/unlock_level #` debug command** (`GRAVITY.MD:40`) — manual level override used during interactive QA, intentionally hidden from auto-complete.

None of these are automated tests. They are runtime safety nets and dev-loop conveniences.

## Common Patterns (would-be)

**Async Testing:** No pattern exists. If introduced, the streaming hot-path in `AIBWindows/Services/OpenAIService.cs:183` (`StreamResponseAsync` returns `IAsyncEnumerable<string>`) would require an `await foreach` consumer and a stub `ChatClient`. The current code constructs `ChatClient` directly inside `EnsureClient()` (referenced from `OpenAIService.cs:72,192`), so seams for mocking do not exist yet — `OpenAIService` would need an injectable client factory before it could be unit-tested.

**Error Testing:** No pattern exists. The tool error string contract (`ERRO: ...`, `ACESSO NEGADO (SANDBOX): ...`, `SUCESSO: ...`) documented in `CONVENTIONS.md` is the natural assertion surface: `await tool.ExecuteAsync(badJson)` should yield a string starting with `ERRO:`. No such test exists.

## Gaps That Should Be Tested

Concrete, high-risk areas with zero coverage and clear test seams:

| Area | File:line | Why it matters |
|------|-----------|----------------|
| `ToolArgParser.Get` JSON fallback chain | `AIBWindows/Services/NativeTools.cs:16-65` | Pure function (string in, string out). Handles malformed LLM JSON — incorrect parsing breaks every tool call. Trivial to unit-test. |
| `RunCommandTool` sandbox levels | `AIBWindows/Services/NativeTools.cs:315-358` | Security boundary. Mis-classified commands at Level 1-7 = privilege escalation. `ContainsWord` regex correctness is also untested. |
| `LevelService.GetLevel` / `GetMaxTokensForLevel` | `AIBWindows/Services/LevelService.cs:10-46` | Pure functions, table-driven, untested. Off-by-one in XP thresholds silently breaks gamification. |
| `MemoryService.CosineSimilarity` | `AIBWindows/Services/MemoryService.cs:65-76` | Pure math. Trivially testable. RAG recall correctness depends on it. |
| `CredentialService` global-fallback search | `AIBWindows/Services/CredentialService.cs:64-78` | Sensitive: cross-system credential leakage if the fallback returns the wrong system's key. |
| `OpenAIService` streaming state machine | `AIBWindows/Services/OpenAIService.cs:240-401` | Most complex code in the project. Three-mode (`Streaming`/`InsideThink`/`WaitingFinal`) plus carry-buffer reconstruction. Lots of inline rationale comments explaining bugs that *did* happen — would benefit hugely from chunk-fixture replay tests. |
| `SettingsService.LoadSettings` DPAPI + legacy fallback | `AIBWindows/Services/SettingsService.cs:49-79` | Migration path. Easy to silently corrupt user profile. |
| `SkillService.ParseMarkdownSkill` YAML-frontmatter parser | `AIBWindows/Services/SkillService.cs:106-129` | Hand-rolled parser; untested edge cases (CRLF, missing terminator). |
| Python `OpenAIClient.send_message` history mutation | `AIBLinux/app/openai_client.py:25-61` | Mutates `self.history` mid-iteration. Easy to test with a fake `OpenAI` stub. |

## Recommendations (informative)

If/when tests are introduced, the natural shape would be:

- **AIBWindows**: new `AIBWindows.Tests/` project using **xUnit** (matches modern .NET 8 defaults), referencing `AIB.csproj`. Target pure services first (`LevelService`, `MemoryService.CosineSimilarity`, `ToolArgParser`, `SkillService.ParseMarkdownSkill`, `RunCommandTool.ContainsWord` sandbox matrix). Mock `ChatClient` only after refactoring `OpenAIService` to accept an injected factory.
- **AIBLinux**: add **pytest** + `pytest-asyncio` (already implicitly approved by `.gitignore` listing `.pytest_cache/` and `.coverage`). Start with `OpenAIClient` using `responses`/`respx` to stub the OpenAI HTTP layer.
- **CI**: add `.github/workflows/ci.yml` running `dotnet build` + `dotnet test` for Windows runners and `pytest` for Linux runners. None exists today.

---

*Testing analysis: 2026-05-28*
