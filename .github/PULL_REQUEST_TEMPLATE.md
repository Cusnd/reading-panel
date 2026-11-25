## Summary

<!-- Describe what this pull request changes and why. -->

## Verification

- [ ] `dotnet test tests/ReadingPanel.Tests/ReadingPanel.Tests.csproj` (Offline edition)
- [ ] `dotnet test tests/ReadingPanel.Tests/ReadingPanel.Tests.csproj -p:OnlineEdition=true` (Online edition)
- [ ] Manual check on Windows (if UI, mouse hook, clipboard, or speech behavior changed)
