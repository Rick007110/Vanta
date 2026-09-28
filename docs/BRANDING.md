# Branding ("Vanta" is a working name)

To rename the product, change these places (nothing else hard-codes the name):

| What | Where |
|---|---|
| Exe name, product/company/version resources | `Directory.Build.props` (`BrandName`, `Version`) |
| Runtime name, data folder (`%LOCALAPPDATA%\<name>`), virtual UI origin, mutex | `src/Vanta.Core/Branding.cs` |
| UI name + logo mark (SVG) | `ui/shared/brand.js` (the UI title and sidebar use it) |
| App icon | `tools/brand/make_icon.py` -> `src/Vanta.App/app.ico` (same geometry as the SVG) |
| Manifest description | `src/Vanta.App/app.manifest` |
| Accent colours | `ui/styles.css` `:root` (`--accent*`) |
| User texts that mention the name | `ui/shared/i18n.js`, `README.txt`, `LEESMIJ.txt`, `CHANGELOG.md`, `src/Vanta.Core/Strings.cs` |

The C# namespaces (`Vanta.*`) are internal and can stay as they are.
