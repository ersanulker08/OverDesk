# Contributing to OverDesk

Thanks for helping! A few things keep the project easy to work on.

## Most wanted

- **Windows testing.** The Windows layer compiles but has never run on a real machine. It covers Win32 window reparenting,
  WebView2, the GPU/NPU gauges and Credential Manager. Bug reports with steps to reproduce are gold.
- **Tools.** New tools for the right-hand panel. Start from [samples/HelloTool](samples/HelloTool) and
  [docs/PLUGINS.md](docs/PLUGINS.md).
- **macOS window handling.** Apps that refuse to stay glued to the stage, or that misbehave with focus mode.

## Ground rules

- **English only**: code, comments, UI text, commit messages and docs.
- **Keep OS code out of `Overdesk.Core`.** New platform features get an interface in `Overdesk.Core/Platform` and one
  implementation per OS (`Overdesk.Platform.MacOS`, `Overdesk.Platform.Windows`). A feature that only works on one OS should
  degrade quietly on the other.
- **Both flavors must build.** From a Mac you can check the Windows one with
  `dotnet build src/Overdesk.App -p:OS=Windows_NT -p:EnableWindowsTargeting=true`. CI builds both.
- **No warnings.** The solution builds with 0 warnings; please keep it that way.
- **Match the look.** Colors, spacing and icons come from `src/Overdesk.App/Theme`. Use the accent brushes as
  `DynamicResource` so theme switching keeps working.
- **Secrets never go in settings.** API keys use `ISecretStore` (system keychain / Credential Manager).

## Workflow

1. Fork, then create a branch: `git checkout -b my-change`.
2. Make your change. Run it with `dotnet run --project src/Overdesk.App`.
   Use `OVERDESK_DATA_DIR=/tmp/od` so your real settings stay untouched.
3. For UI changes, add a screenshot to the pull request. `OVERDESK_SNAPSHOT=/tmp/shots` renders the main screens for you.
4. Open a pull request that explains what changed and why, and on which OS you tested it.

By contributing you agree that your work is released under the [MIT License](LICENSE).
