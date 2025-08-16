# 🚀 Contributing to Nino

This comprehensive guide covers both contributing to the Nino project and understanding our modern CI/CD workflows.

## 🤝 How to Contribute

### Quick Start for Contributors

1. **Fork the repository** on GitHub
2. **Clone your fork** locally:
   ```bash
   git clone https://github.com/YOUR_USERNAME/Nino.git
   cd Nino
   ```
3. **Create a feature branch**:
   ```bash
   git checkout -b feature/your-feature-name
   ```
4. **Make your changes** following our guidelines below
5. **Test your changes** locally
6. **Create a Pull Request**

### 📋 Contribution Types

We welcome various types of contributions:

- 🐛 **Bug fixes** - Fix existing issues
- ✨ **New features** - Add functionality to Nino
- 📚 **Documentation** - Improve docs, examples, or guides
- 🎨 **Code improvements** - Refactoring, performance optimizations
- 🧪 **Tests** - Add or improve test coverage
- 🔧 **Tooling** - Improve build scripts, CI/CD, or development tools

## 🛠️ Development Setup

### Prerequisites

- **.NET SDK 8.0+** (with 6.0 and 2.1 for multi-targeting)
- **Unity 2022.3.51f1** (for Unity development)
- **Git** for version control
- **IDE** (Visual Studio, VS Code, or JetBrains Rider)

### Local Development

1. **Build the solution**:
   ```bash
   cd src
   dotnet restore
   dotnet build
   ```

2. **Run tests**:
   ```bash
   dotnet test
   ```

3. **Run benchmarks** (optional):
   ```bash
   cd Nino.Benchmark
   dotnet run -c Release
   ```

### Unity Development

1. **Copy .NET DLLs to Unity**:
   ```bash
   cp ./Nino.Core/bin/Debug/netstandard2.1/Nino.Core.dll ./Nino.Unity/Packages/com.jasonxudeveloper.nino/Runtime/
   cp ./Nino.Generator/bin/Debug/netstandard2.1/Nino.Generator.dll ./Nino.Unity/Packages/com.jasonxudeveloper.nino/Runtime/
   ```

2. **Open Unity project** at `src/Nino.Unity`
3. **Run Unity tests** in the Test Runner window

## 📝 Coding Guidelines

### Code Style

- **Follow existing patterns** in the codebase
- **Use meaningful names** for variables, methods, and classes
- **Write self-documenting code** with clear intent
- **Add XML documentation** for public APIs
- **Keep methods focused** - single responsibility principle

### Commit Message Format

We use **Conventional Commits** for better release notes:

```
type(scope): description

[optional body]

[optional footer]
```

**Types:**
- `feat`: New features
- `fix`: Bug fixes
- `docs`: Documentation changes
- `style`: Code style changes (formatting, etc.)
- `refactor`: Code refactoring
- `test`: Adding/updating tests
- `chore`: Maintenance tasks

**Examples:**
```bash
feat(serialization): add support for nullable reference types
fix(generator): resolve issue with nested generic types
docs(readme): update installation instructions
test(core): add unit tests for buffer writer
```

### Testing Requirements

- **Add tests** for new features
- **Update tests** when modifying existing code
- **Ensure all tests pass** before submitting PR
- **Test both .NET and Unity** scenarios when applicable
- **Include edge cases** and error conditions

## 🔄 Pull Request Process

### Before Creating a PR

1. **Sync with upstream**:
   ```bash
   git remote add upstream https://github.com/JasonXuDeveloper/Nino.git
   git fetch upstream
   git rebase upstream/main
   ```

2. **Run full test suite**:
   ```bash
   dotnet test --verbosity normal
   ```

3. **Check code builds** in Release mode:
   ```bash
   dotnet build -c Release
   ```

### Creating the PR

1. **Push your branch**:
   ```bash
   git push origin feature/your-feature-name
   ```

2. **Create PR** on GitHub with:
   - **Clear title** describing the change
   - **Detailed description** explaining what and why
   - **Link to issues** if applicable
   - **Screenshots/examples** for UI changes

### PR Template

```markdown
## Description
Brief description of changes

## Type of Change
- [ ] Bug fix
- [ ] New feature
- [ ] Documentation update
- [ ] Performance improvement
- [ ] Other: ___________

## Testing
- [ ] Added/updated unit tests
- [ ] Tested on .NET
- [ ] Tested on Unity
- [ ] All tests pass

## Checklist
- [ ] Code follows project style guidelines
- [ ] Self-review completed
- [ ] Documentation updated if needed
- [ ] No breaking changes (or clearly documented)
```

### Review Process

1. **Automated checks** will run (CI/CD pipeline)
2. **Maintainer review** - expect feedback and iteration
3. **Address feedback** by pushing additional commits
4. **Squash and merge** once approved

## 🎯 CI/CD Pipeline

Our automated systems will:

### ✅ On Every PR/Push
- **Build** .NET solution and Unity project
- **Run tests** for both platforms
- **Check code quality** and style
- **Run benchmarks** on main branch
- **Report results** in PR comments

### 🚫 PR Blocking Conditions
- Build failures
- Test failures
- Merge conflicts
- Missing required reviews

## 🏷️ Release Process (Maintainers)

For project maintainers managing releases:

## 📋 CI/CD Overview

Our automated workflow system provides:

- ✅ **Build and test for all commits** - CI runs on every push and PR
- ✅ **Modern semantic versioning** - Support for stable and pre-releases (alpha, beta, rc)
- ✅ **Tag-based releases** - No more commit message parsing
- ✅ **Automatic release notes** - Generated from commit history
- ✅ **GitHub Actions best practices** - Latest actions, caching, concurrency control
- ✅ **Integrated benchmarks** - Automatic performance reports on releases
- ✅ **Environment protection** - Safe deployment to NuGet

*This ensures every contribution is automatically tested and validated.*

## 🔄 Workflows

### 1. CI - Build and Test (`ci.yml`)

**Triggers:** Push/PR to `main` or `develop` branches

**Features:**
- Builds and tests both .NET and Unity projects
- Runs on every commit for early feedback
- Caches dependencies for faster builds
- Uploads test results and coverage
- Runs benchmarks on main branch pushes (but doesn't update releases)

### 2. Release (`release.yml`)

**Triggers:** Push of version tags (`v*`)

**Features:**
- Validates semantic version format with security checks
- **Pre-release support**: alpha, beta, rc with proper NuGet/Unity handling
- **Smart version management**: Different formats for NuGet vs Unity UPM
- **Auto-generated release notes**: Includes PR titles, contributors, and installation instructions
- Builds, tests, and publishes to NuGet with retry logic
- **Unity UPM handling**: Stable releases update main branch, pre-releases create separate branches
- Environment protection and comprehensive error handling

### 3. Benchmark and Performance Report (`report.yml`)

**Triggers:** 
- Release published events
- Manual dispatch
- Called from release workflow

**Features:**
- Runs comprehensive benchmarks
- Updates release notes with performance data
- Stores benchmark artifacts
- Prevents duplicate benchmark runs

## 🏷️ Creating Releases (Maintainers Only)

### 🔄 Complete Release Process

When you create a release tag, the CI automatically:

1. **✅ Validates** the tag format and version
2. **🔍 Verifies** CI passed for the commit (waits if still running)
3. **📝 Updates** all version files (Version.cs, .csproj, package.json)
4. **🏷️ Moves** the tag to point to the updated commit
5. **📚 Copies** Release DLLs to Unity package folder
6. **🌱 Creates** appropriate branches (main for stable, release/* for pre-releases)
7. **📦 Publishes** to NuGet with proper versioning
8. **📊 Runs** benchmarks and updates release notes
9. **🧹 Cleans up** pre-release branches after successful completion

### Easy Way: Use the Helper Script

```bash
# Stable release
./create-release.sh 1.2.3

# Pre-releases (all supported)
./create-release.sh 1.2.3 alpha  # Creates v1.2.3-alpha.1
./create-release.sh 1.2.3 beta   # Creates v1.2.3-beta.1  
./create-release.sh 1.2.3 rc     # Creates v1.2.3-rc.1

# Sequential releases auto-increment
./create-release.sh 1.2.3 alpha  # Creates v1.2.3-alpha.2 (if alpha.1 exists)
./create-release.sh 1.2.3 beta   # Creates v1.2.3-beta.2 (if beta.1 exists)
```

⚠️ **Important:** You only create the tag - CI handles everything else automatically!

### 🔍 Smart CI Verification

The release workflow intelligently handles CI status:

- **✅ CI Already Passed**: Release continues immediately
- **⏳ CI Still Running**: Waits up to 30 minutes for completion
- **❌ CI Failed**: Blocks release with clear error message  
- **⚠️ No CI Found**: Warns but continues (for edge cases)

This ensures every release is from tested code while avoiding redundant test runs.

### 🏷️ Smart Tag Management

**Important:** The release workflow automatically moves your tag to ensure consistency:

1. **You create tag** pointing to commit A (your latest code)
2. **CI updates versions** and creates commit B (with version files + DLLs) 
3. **CI moves your tag** to point to commit B (the actual release)
4. **Release artifacts** are built from commit B (correct versions)

This ensures the release tag always points to the exact commit that becomes the release, including all version updates and Unity DLLs.

### Manual Way: Git Tags

```bash
# Stable release
git tag -a v1.2.3 -m "Release v1.2.3"
git push origin v1.2.3

# Pre-release
git tag -a v1.2.3-beta.1 -m "Release v1.2.3-beta.1"
git push origin v1.2.3-beta.1
```

## 📊 Version Management

### 🎯 Smart Version Handling

The workflow automatically handles different versioning requirements:

**Stable Releases (`v1.2.3`):**
- `src/Version.cs` - Assembly versions: `1.2.3`
- `.csproj files` - NuGet versions: `1.2.3`
- `package.json` - Unity UPM: `1.2.3`
- Unity packages pushed to `main` branch

**Pre-releases (`v1.2.3-beta.1`):**
- `src/Version.cs` - Assembly versions: `1.2.3` (base version)
- `.csproj files` - NuGet versions: `1.2.3-beta.1` (full semantic version)
- `package.json` - Unity UPM: `1.2.3-preview.1` (Unity-compatible format)
- Unity packages pushed to `release/1.2.3-beta.1` branch (not main)

### 📄 What CI Updates Automatically:

**Version Files:**
- `src/Version.cs` - Assembly versions  
- `src/Nino.Core/Nino.Core.csproj` - Core package version
- `src/Nino/Nino.csproj` - Main package version
- `src/Nino.Generator/Nino.Generator.csproj` - Generator package version
- `src/Nino.Unity/Packages/com.jasonxudeveloper.nino/package.json` - Unity package version

**Unity Package DLLs:**
- Copies `Nino.Core.dll` (Release build) to Unity Runtime folder
- Copies `Nino.Generator.dll` (Release build) to Unity Runtime folder
- Commits these to appropriate branch (main for stable, release/* for pre-releases)

**NuGet Packages:**
- Builds and publishes all packages with correct versioning
- Handles pre-release suffixes properly (`1.2.3-beta.1`)

## 🔒 Security and Environment Protection

- **Production environment** protection for NuGet publishing
- **Secrets management** for Unity license and NuGet API keys
- **Concurrency control** to prevent parallel releases
- **Validation steps** before any deployment

## 📈 Monitoring and Observability

- **GitHub Actions summaries** with release status
- **Artifact uploads** for test results and benchmarks
- **Release notifications** in workflow outputs
- **Performance tracking** via benchmark reports

## 🔧 Configuration

### Required Secrets

- `UNITY_EMAIL` - Unity license email
- `UNITY_PASSWORD` - Unity license password  
- `UNITY_SERIAL` - Unity license serial
- `MYTOKEN` - NuGet API key

### Optional Variables

- `DOTNET_VERSION` - .NET version override (defaults to 8.0.x)

### Environment Protection (Recommended)

Set up environment protection for `production` and `nuget-production` environments in your repository settings for additional security.

## 🎯 Complete Release Type Matrix

| Type | Command | Git Tag | NuGet | Unity UPM | Branch | Use Case |
|------|---------|---------|-------|-----------|--------|-----------|
| **Stable** | `./create-release.sh 1.2.3` | `v1.2.3` | `1.2.3` | `1.2.3` | `main` | Production ready |
| **Alpha** | `./create-release.sh 1.2.3 alpha` | `v1.2.3-alpha.1` | `1.2.3-alpha.1` | `1.2.3-preview.1` | `release/1.2.3-alpha.1` | Early development |
| **Beta** | `./create-release.sh 1.2.3 beta` | `v1.2.3-beta.1` | `1.2.3-beta.1` | `1.2.3-preview.1` | `release/1.2.3-beta.1` | Feature complete |
| **RC** | `./create-release.sh 1.2.3 rc` | `v1.2.3-rc.1` | `1.2.3-rc.1` | `1.2.3-preview.1` | `release/1.2.3-rc.1` | Final testing |

### 🔄 Sequential Numbering
- Multiple pre-releases auto-increment: `alpha.1`, `alpha.2`, `beta.1`, `rc.1`, etc.
- Each type maintains its own counter
- Example flow: `v1.2.3-alpha.1` → `v1.2.3-alpha.2` → `v1.2.3-beta.1` → `v1.2.3-rc.1` → `v1.2.3`

### ❓ **FAQ: Do I Need to Manually Copy DLLs or Update package.json?**

**✅ No!** The release workflow automatically:
1. Builds Release configuration DLLs
2. Copies them to Unity package Runtime folder  
3. Updates package.json with correct version format
4. Commits changes to the appropriate branch
5. Publishes to NuGet

**You only need to create the version tag - everything else is automated!**

### 🎮 Unity UPM & Branch Strategy

**❓ Do I need to create branches manually?**
**✅ No!** The CI automatically handles all branching:

- **Stable releases** (`v1.2.3`): CI updates `main` branch directly
- **Pre-releases** (`v1.2.3-beta.1`): CI automatically creates `release/1.2.3-beta.1` branches
- **You only create tags** - CI handles the rest

**Unity UPM Compatibility:**
- Unity doesn't support semantic pre-release format (`-alpha.1`, `-beta.1`)
- All pre-releases use Unity's `-preview.N` format for compatibility
- Unity Package Manager auto-detects updates on these branches

## 💡 Best Practices

### For Contributors
1. **Start small** - Begin with bug fixes or documentation
2. **Discuss big changes** - Open an issue before major features
3. **Follow conventions** - Use existing patterns and styles
4. **Write tests** - Ensure your changes are well-tested
5. **Document changes** - Update docs for new features
6. **Be responsive** - Address review feedback promptly

### For Maintainers
1. **Follow the release lifecycle**: alpha → beta → rc → stable
2. **Use the helper script** for consistent tag creation
3. **Monitor workflow runs** in the Actions tab
4. **Check benchmark results** in release notes
5. **Keep commit messages descriptive** - They become release notes
6. **Use conventional commits** for better auto-generated release notes

## 🌟 Recognition

Contributors are recognized in:
- **Release notes** - Auto-generated contributor lists
- **GitHub contributors** - Visible on the repository
- **Special mentions** - For significant contributions

## 💬 Getting Help

- **GitHub Issues** - For bugs and feature requests
- **GitHub Discussions** - For questions and general discussion
- **Pull Request comments** - For code-specific questions
- **Review workflow logs** - For CI/CD issues

---

# 🔧 CI/CD Technical Documentation

*The following sections detail our automated systems for maintainers and advanced contributors.*

## 🆘 Troubleshooting

### Common Issues

1. **Tag already exists**: Delete the tag locally and remotely, then recreate
   ```bash
   git tag -d v1.2.3
   git push origin :refs/tags/v1.2.3
   ```

2. **Unity tests failing**: Check Unity license secrets are properly configured

3. **NuGet publish failing**: Verify `MYTOKEN` secret and package versions

4. **Benchmark not running**: Check if it was already run for this release

### Getting Help

- Check the workflow run logs in GitHub Actions
- Review the job summaries for detailed status
- Validate your git tags match semantic versioning format
- Ensure all required secrets are configured

## 🤝 Community Guidelines

### Code of Conduct

We are committed to providing a welcoming and inclusive environment:

- **Be respectful** - Treat everyone with respect and kindness
- **Be constructive** - Provide helpful feedback and suggestions
- **Be patient** - Help others learn and grow
- **Be inclusive** - Welcome contributors from all backgrounds

### Issue Reporting

When reporting bugs:

1. **Search existing issues** first
2. **Use issue templates** when available
3. **Provide reproduction steps**
4. **Include environment details** (.NET version, Unity version, OS)
5. **Attach logs/screenshots** when relevant

### Feature Requests

1. **Check roadmap** and existing issues
2. **Explain the use case** clearly
3. **Describe expected behavior**
4. **Consider backwards compatibility**
5. **Be open to discussion** and alternatives

## 📚 Additional Resources

- **[Nino Documentation](https://nino.xgamedev.net/en/)** - Complete usage guide
- **[GitHub Repository](https://github.com/JasonXuDeveloper/Nino)** - Source code and issues
- **[Release Notes](https://github.com/JasonXuDeveloper/Nino/releases)** - What's new in each version
- **[Benchmarks](https://github.com/JasonXuDeveloper/Nino/actions/workflows/report.yml)** - Performance comparisons

---

*Thank you for contributing to Nino! 🎉 Your contributions help make high-performance serialization accessible to everyone.*