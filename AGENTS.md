# ScreenLingo delivery

- The permanent public application name is **ScreenLingo**, including the EXE, window titles, About screen and release names. Keep the internal namespace and existing preference directory compatible.
- Update the project version and manifest version for each delivered application update. Display the compiled version in the UI.
- Every visible control must use the selected skin, including scrollbars, sliders, checkboxes, text fields and keyboard focus indicators. Retain native control behavior and accessibility.
- After changing the application, run `Publish.ps1 -PackageDirectory <absolute-package-output-directory>` and inspect its appearance report. It builds, checks and refreshes the same `Desktop\ScreenLingo\ScreenLingo.exe` location automatically. Run additional checks appropriate to behavior changes.
- Keep the source and packages together in the task's `outputs` directory. Do not commit without an explicit request.
