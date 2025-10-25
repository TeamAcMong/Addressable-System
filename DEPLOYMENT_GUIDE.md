# Deployment Guide - Unity Addressables Manager

## 📦 Publishing Package to Unity Package Manager (UPM)

This guide will help you publish the **AddressableManager** package so it can be installed via Unity Package Manager.

---

## 🎯 Deployment Options

### Option 1: Git URL (Recommended)
Install directly from GitHub repository

### Option 2: Local Package
Install from local folder

### Option 3: UPM Registry (Advanced)
Publish to npm registry (scoped packages)

---

## 📋 Prerequisites

Before deploying, ensure:

- [x] All bugs fixed ✅
- [x] Code compiles without errors ✅
- [x] Documentation complete ✅
- [x] Package.json properly configured ✅
- [x] Git repository initialized

---

## 🚀 Option 1: Deploy via Git URL (Easiest)

### Step 1: Prepare Git Repository

```bash
# Navigate to project root
cd "D:\GitRepo\Addressable System"

# Initialize git (if not already)
git init

# Add .gitignore for Unity
```

Create `.gitignore`:
```gitignore
# Unity
[Ll]ibrary/
[Tt]emp/
[Oo]bj/
[Bb]uild/
[Bb]uilds/
[Ll]ogs/
[Uu]ser[Ss]ettings/

# Visual Studio cache directory
.vs/

# Rider
.idea/

# OS
.DS_Store
Thumbs.db

# Unity meta files (keep .meta files!)
# *.meta
```

### Step 2: Organize Package Structure

Move package to proper location:

```bash
# Current location: Assets/com.game.addressables/
# Needs to be at: Packages/com.game.addressables/

# Option A: Keep in Assets (works but not ideal)
# Option B: Move to Packages folder (recommended)
```

**Recommended Structure**:
```
YourRepo/
├── Packages/
│   └── com.game.addressables/
│       ├── Runtime/
│       ├── package.json
│       ├── README.md
│       ├── CHANGELOG.md
│       └── LICENSE.md
└── README.md (repo readme)
```

### Step 3: Update package.json

Ensure package.json has all required fields:

```json
{
  "name": "com.game.addressables",
  "version": "1.0.3",
  "displayName": "Addressable Manager",
  "description": "Production-ready Addressable Asset Management System with scope-based lifecycle, object pooling, and progress tracking",
  "unity": "2021.3",
  "keywords": [
    "addressables",
    "asset management",
    "resource management",
    "pooling",
    "lifecycle"
  ],
  "author": {
    "name": "Game Team",
    "email": "your-email@example.com",
    "url": "https://github.com/yourusername/addressable-manager"
  },
  "dependencies": {
    "com.unity.addressables": "2.3.1"
  },
  "repository": {
    "type": "git",
    "url": "https://github.com/yourusername/addressable-manager.git"
  }
}
```

### Step 4: Create GitHub Repository

1. **Create new repo on GitHub**:
   - Name: `addressable-manager` (or your choice)
   - Public or Private
   - Don't initialize with README (we have one)

2. **Push to GitHub**:

```bash
# Add remote
git remote add origin https://github.com/yourusername/addressable-manager.git

# Add files
git add .

# Commit
git commit -m "Initial release v1.0.3 - Production ready"

# Push
git push -u origin main

# Create release tag
git tag v1.0.3
git push origin v1.0.3
```

### Step 5: Install in Unity Projects

**Method 1: Package Manager UI**

1. Open Unity project
2. Window → Package Manager
3. Click `+` → Add package from git URL
4. Enter: `https://github.com/yourusername/addressable-manager.git`

**Method 2: manifest.json**

Edit `Packages/manifest.json`:
```json
{
  "dependencies": {
    "com.game.addressables": "https://github.com/yourusername/addressable-manager.git",
    "com.unity.addressables": "2.3.1"
  }
}
```

**Method 3: Specific Version**
```json
{
  "dependencies": {
    "com.game.addressables": "https://github.com/yourusername/addressable-manager.git#v1.0.3"
  }
}
```

---

## 📁 Option 2: Local Package Installation

### For Development/Testing

**Step 1: Copy Package**

Copy the package folder to target Unity project:
```
TargetProject/Packages/com.game.addressables/
```

**Step 2: Unity Detects Automatically**

Unity will automatically detect and import the package.

**Alternative: Add via manifest.json**

```json
{
  "dependencies": {
    "com.game.addressables": "file:../../path/to/com.game.addressables"
  }
}
```

---

## 🌐 Option 3: Publish to npm Registry (Advanced)

### For Public Distribution

**Step 1: Setup npm Package**

```bash
# Login to npm
npm login

# Publish (from package root)
cd Packages/com.game.addressables
npm publish --access public
```

**Step 2: Install in Unity**

Add to `manifest.json`:
```json
{
  "scopedRegistries": [
    {
      "name": "npmjs",
      "url": "https://registry.npmjs.org/",
      "scopes": ["com.game"]
    }
  ],
  "dependencies": {
    "com.game.addressables": "1.0.3"
  }
}
```

---

## 📝 Package Checklist Before Release

### Required Files

- [x] `package.json` - Package metadata
- [x] `README.md` - Documentation
- [x] `CHANGELOG.md` - Version history
- [x] `LICENSE.md` - MIT License
- [x] `Runtime/` folder - All code
- [x] `.asmdef` files - Assembly definitions

### Optional But Recommended

- [ ] `Tests/` - Unit tests
- [ ] `Editor/` - Editor tools
- [ ] `Documentation~/` - Detailed docs
- [ ] `Samples~/` - Example scenes

### Quality Checklist

- [x] Code compiles without errors
- [x] No console warnings
- [x] All namespaces use `AddressableManager`
- [x] Documentation complete
- [x] Examples provided
- [x] All bugs fixed

---

## 🏷️ Version Tagging Strategy

### Semantic Versioning (SemVer)

```
MAJOR.MINOR.PATCH
```

- **MAJOR**: Breaking changes (namespace change, API changes)
- **MINOR**: New features (backward compatible)
- **PATCH**: Bug fixes

**Examples**:
- `v1.0.0` - Initial release
- `v1.0.1` - Bug fix (Convert<object>)
- `v1.0.2` - Bug fix (namespace conflict)
- `v1.0.3` - Bug fix (label loading) ← **Current**
- `v1.1.0` - Add new feature (Builder pattern)
- `v2.0.0` - Breaking change (major API refactor)

### Git Tagging

```bash
# Create tag
git tag -a v1.0.3 -m "Release v1.0.3 - All bugs fixed"

# Push tag
git push origin v1.0.3

# List tags
git tag -l

# Delete tag (if mistake)
git tag -d v1.0.3
git push origin :refs/tags/v1.0.3
```

---

## 📦 Package Structure for UPM

### Minimal Structure
```
com.game.addressables/
├── Runtime/
│   ├── AddressableManager.asmdef
│   └── (all .cs files)
├── package.json
└── README.md
```

### Full Structure (Recommended)
```
com.game.addressables/
├── Runtime/
│   ├── Core/
│   ├── Loaders/
│   ├── Scopes/
│   ├── Pooling/
│   ├── Progress/
│   ├── Facade/
│   └── AddressableManager.asmdef
├── Tests/                         (Optional)
│   ├── Runtime/
│   └── Editor/
├── Editor/                        (Optional)
│   └── Tools/
├── Documentation~/                (Optional)
│   ├── images/
│   └── guides/
├── Samples~/                      (Optional)
│   └── BasicExample/
├── package.json                   (Required)
├── README.md                      (Required)
├── CHANGELOG.md                   (Recommended)
└── LICENSE.md                     (Recommended)
```

---

## 🔄 Update Process

### Releasing Updates

**Step 1: Update Version**

Edit `package.json`:
```json
{
  "version": "1.0.4"  // Increment version
}
```

**Step 2: Update CHANGELOG**

Add to `CHANGELOG.md`:
```markdown
## [1.0.4] - 2025-01-XX

### Fixed
- Bug XYZ fixed

### Added
- New feature ABC
```

**Step 3: Commit & Tag**

```bash
git add .
git commit -m "Release v1.0.4 - Bug fixes"
git tag v1.0.4
git push origin main
git push origin v1.0.4
```

**Step 4: GitHub Release**

1. Go to GitHub → Releases
2. Click "Create new release"
3. Select tag `v1.0.4`
4. Title: `v1.0.4 - Bug Fixes`
5. Description: Copy from CHANGELOG
6. Publish release

---

## 🧪 Testing Before Release

### Local Testing

```bash
# Create test Unity project
# Add package locally
# Test all features
# Verify no errors
```

### Checklist

- [ ] Package installs successfully
- [ ] No compilation errors
- [ ] Examples work
- [ ] Documentation accessible
- [ ] Dependencies resolved

---

## 📚 Documentation in Package

### README.md Template

```markdown
# Addressable Manager

Production-ready Unity Addressables management system.

## Installation

### Via Git URL
```json
{
  "dependencies": {
    "com.game.addressables": "https://github.com/yourusername/addressable-manager.git"
  }
}
```

### Via Package Manager
1. Window → Package Manager
2. + → Add from git URL
3. Enter: `https://github.com/yourusername/addressable-manager.git`

## Quick Start
[Include basic usage example]

## Documentation
See [Documentation](link) for full guide.

## License
MIT License
```

---

## 🎯 Quick Deploy Script

Create `deploy.sh`:

```bash
#!/bin/bash

# Deploy script for Addressable Manager

VERSION=$1

if [ -z "$VERSION" ]; then
  echo "Usage: ./deploy.sh <version>"
  echo "Example: ./deploy.sh 1.0.4"
  exit 1
fi

echo "Deploying version $VERSION..."

# Update package.json version
# (Manual or use jq tool)

# Commit
git add .
git commit -m "Release v$VERSION"

# Tag
git tag "v$VERSION"

# Push
git push origin main
git push origin "v$VERSION"

echo "✅ Deployed v$VERSION successfully!"
```

Usage:
```bash
chmod +x deploy.sh
./deploy.sh 1.0.4
```

---

## 🌟 Best Practices

### DO's
- ✅ Use semantic versioning
- ✅ Keep CHANGELOG updated
- ✅ Tag every release
- ✅ Test before publishing
- ✅ Include documentation
- ✅ Use descriptive commit messages

### DON'Ts
- ❌ Don't change versions without tagging
- ❌ Don't skip testing
- ❌ Don't publish with errors
- ❌ Don't forget dependencies
- ❌ Don't delete old tags (breaks installs)

---

## 🔗 Useful Resources

### Unity Package Manager
- [UPM Documentation](https://docs.unity3d.com/Manual/upm-ui.html)
- [Custom Packages](https://docs.unity3d.com/Manual/CustomPackages.html)
- [Package Manifest](https://docs.unity3d.com/Manual/upm-manifestPkg.html)

### Git
- [Git Tagging](https://git-scm.com/book/en/v2/Git-Basics-Tagging)
- [GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github)

---

## ✅ Final Checklist

Before deploying to production:

- [ ] All bugs fixed
- [ ] Tests pass
- [ ] Documentation complete
- [ ] Examples working
- [ ] package.json correct
- [ ] Version tagged
- [ ] CHANGELOG updated
- [ ] README clear
- [ ] License included
- [ ] Repository public (if open-source)

---

## 🎉 You're Ready to Deploy!

Follow the steps above to publish your package.

**Recommended**: Start with Option 1 (Git URL) for easiest deployment.

Good luck! 🚀

---

*Last Updated: January 2025*
*Version: 1.0.3*
