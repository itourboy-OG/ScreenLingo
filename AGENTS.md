# ScreenLingo delivery

- The permanent public application name is **ScreenLingo**, including the EXE, window titles, About screen and release names. Keep the internal namespace and existing preference directory compatible.
- Update the project version and manifest version for each delivered application update. Display the compiled version in the UI.
- Every visible control must use the selected skin, including scrollbars, sliders, checkboxes, text fields and keyboard focus indicators. Retain native control behavior and accessibility.
- After changing the application, run `Publish.ps1 -PackageDirectory <absolute-package-output-directory>` and inspect the appearance report, plus checks appropriate to the behavior changes. It builds and checks a separate package; existing Desktop and installed copies stay untouched.
- Never install or upgrade the user's application, including silent installer verification, unless the user explicitly requests installation. Build and publish the update, tell the user it is ready, and let them install it themselves. Preserve the version they are using so they can test the startup check, update banner, download progress and installation flow manually.
- Public Windows downloads use the branded `ScreenLingo-<version>-Setup-x64.exe`. Build with `Build-Installer.ps1` after publishing, and keep `Desktop\ScreenLingo\ScreenLingo Setup.exe` current. Preserve the explicitly documented 0.2.0 ZIP update bridge while that older version is supported.
- Keep the source and packages together in the task's `outputs` directory. Do not commit without an explicit request.
- Keep the GitHub front page's screenshots current. Public release notes must explain shipped additions, changes and fixes, include relevant testing limits, and label future plans as provisional. Do not invent fixes or promise untested game compatibility.
