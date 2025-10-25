# Git Subtree Deployment Guide - Addressable Manager

## 🚀 Professional UPM Deployment Using Git Subtree

This guide explains how to deploy your package using **git subtree**, which creates a clean `upm` branch containing only package files.

---

## ✨ Why Git Subtree?

### Advantages Over Basic Git URL Method

| Feature | Basic Git URL | Git Subtree (This Method) |
|---------|---------------|---------------------------|
| **Install Size** | Entire Unity project | Package files only |
| **Install Speed** | Slower (large repo) | Faster (small repo) |
| **Clean History** | Unity project commits | Package-only commits |
| **Professional** | Good | Production-grade |
| **Used By** | Small packages | Unity, major packages |

### What Gets Included

**Basic Git URL** installs:
```
YourRepo/
├── Assets/
├── Library/ (ignored but cloned)
├── Temp/ (ignored but cloned)
├── ProjectSettings/
├── Packages/
└── com.game.addressables/  ← Only this is needed!
```

**Git Subtree** installs:
```
com.game.addressables/  ← Only the package!
├── Runtime/
├── package.json
├── README.md
├── CHANGELOG.md
└── LICENSE.md
```

**Result**: 95% smaller download, faster installs, cleaner!

---

## 📋 Prerequisites

### First-Time Setup

1. **Initialize Git Repository** (if not done)
```bash
cd "D:\GitRepo\Addressable System"
git init
git add .
git commit -m "Initial commit - Addressable Manager v1.0.3"
```

2. **Create GitHub Repository**
   - Go to: https://github.com/new
   - Name: `addressable-manager`
   - Public or Private
   - **Don't** initialize with README
   - Click "Create repository"

3. **Add Remote**
```bash
git remote add origin https://github.com/YOUR_USERNAME/addressable-manager.git
git push -u origin main
```

---

## 🎯 Deployment Process

### Method 1: Using deploy.sh Script (Recommended)

#### Step 1: Make Script Executable

```bash
chmod +x deploy.sh
```

#### Step 2: Update package.json Version

Before deploying, update the version in `Assets/com.game.addressables/package.json`:

```json
{
  "name": "com.game.addressables",
  "version": "1.0.3",  ← Update this
  ...
}
```

#### Step 3: Update CHANGELOG.md

Add release notes to `Assets/com.game.addressables/CHANGELOG.md`:

```markdown
## [1.0.3] - 2025-01-XX

### Fixed
- Fixed LoadAssetsByLabelAsync type mismatch
- Added SharedListOperationTracker and ListItemHandle classes
- Proper reference counting for label-loaded assets
```

#### Step 4: Commit Changes

```bash
git add .
git commit -m "Release v1.0.3 - All bugs fixed, production ready"
git push origin main
```

#### Step 5: Run Deploy Script

```bash
./deploy.sh --semver "1.0.3"
```

**What the script does**:
1. Extracts `Assets/com.game.addressables/` folder
2. Creates a clean `upm` branch with only package files
3. Tags the version as `1.0.3`
4. Pushes tags to GitHub
5. Cleans up temporary branches

**Output**:
```
================================
Deploying Addressable Manager
Version: 1.0.3
Prefix: Assets/com.game.addressables
Branch: upm
================================
Step 1/5: Splitting package from main branch...
Step 2/5: Creating tag 1.0.3...
Step 3/5: Pushing to origin...
Step 4/5: Cleaning up remote branch...
Step 5/5: Cleaning up local branch...
================================
✅ Deployment Complete!

Installation URL for users:
https://github.com/YOUR_USERNAME/addressable-manager.git#1.0.3
================================
```

#### Step 6: Verify on GitHub

1. Go to your repository on GitHub
2. Check "Tags" - should see `1.0.3`
3. Switch to tag `1.0.3` - should only see package files

---

### Method 2: Manual Step-by-Step (Educational)

If you want to understand what the script does:

```bash
# 1. Split package folder into separate branch
git subtree split --prefix="Assets/com.game.addressables" --branch upm

# 2. Tag the version
git tag 1.0.3 upm

# 3. Push tags to remote
git push origin upm --tags

# 4. Delete remote branch (optional, keeps tags)
git push origin --delete upm

# 5. Delete local branch
git branch -D upm
```

---

## 📦 Installation for End Users

### Via Package Manager UI

1. Open Unity project
2. **Window** → **Package Manager**
3. Click **`+`** → **Add package from git URL**
4. Enter: `https://github.com/YOUR_USERNAME/addressable-manager.git#1.0.3`
5. Click **Add**

### Via manifest.json

Edit `Packages/manifest.json`:

```json
{
  "dependencies": {
    "com.game.addressables": "https://github.com/YOUR_USERNAME/addressable-manager.git#1.0.3",
    "com.unity.addressables": "2.3.1"
  }
}
```

### Latest Version (No Tag)

To always get the latest tagged version:

```json
{
  "dependencies": {
    "com.game.addressables": "https://github.com/YOUR_USERNAME/addressable-manager.git"
  }
}
```

**Note**: This installs the latest tag, not the `main` branch!

---

## 🔄 Updating Package (Future Releases)

### For Version 1.0.4 (Example)

#### Step 1: Make Your Changes

Edit code, fix bugs, add features...

#### Step 2: Update Version Number

`Assets/com.game.addressables/package.json`:
```json
{
  "version": "1.0.4"  ← Increment
}
```

#### Step 3: Update CHANGELOG

`Assets/com.game.addressables/CHANGELOG.md`:
```markdown
## [1.0.4] - 2025-02-XX

### Added
- New feature XYZ

### Fixed
- Bug ABC
```

#### Step 4: Commit to Main

```bash
git add .
git commit -m "Release v1.0.4 - New features and bug fixes"
git push origin main
```

#### Step 5: Deploy New Version

```bash
./deploy.sh --semver "1.0.4"
```

**Done!** Users can now update to 1.0.4 in Package Manager.

---

## 📊 Understanding Git Subtree

### What is Git Subtree Split?

Git subtree split extracts a **subdirectory** from your repository and creates a new branch containing **only that subdirectory's history**.

**Example**:

**Before** (main branch):
```
YourRepo/
├── Assets/
│   ├── Scenes/
│   ├── Scripts/
│   └── com.game.addressables/  ← We want this
├── ProjectSettings/
└── Packages/
```

**After** (upm branch created by split):
```
com.game.addressables/  ← Root is the package!
├── Runtime/
├── package.json
├── README.md
└── ...
```

### Why Delete the Branch?

```bash
git push origin --delete upm
git branch -D upm
```

**Q**: Why delete the branch after pushing tags?

**A**:
- **Tags are permanent**: Once pushed, tags stay forever
- **Branches are temporary**: We only need them to create tags
- **Cleaner repo**: No unnecessary branches cluttering your repo
- **Next deploy**: Script recreates the branch fresh

**Result**: Your GitHub repo only has `main` branch + version tags (1.0.3, 1.0.4, etc.)

---

## 🧪 Testing Your Deployment

### Create Test Unity Project

```bash
# 1. Create new Unity project
# 2. Open Package Manager
# 3. Add from git URL: https://github.com/YOUR_USERNAME/addressable-manager.git#1.0.3
# 4. Verify installation
```

### Test Script

Create `Assets/TestDeployment.cs`:

```csharp
using UnityEngine;
using AddressableManager.Facade;

public class TestDeployment : MonoBehaviour
{
    void Start()
    {
        Debug.Log("✅ Addressable Manager installed successfully!");
        Debug.Log($"Package version: 1.0.3");

        // Test that namespace works
        var facade = typeof(Assets);
        Debug.Log($"✅ Facade type found: {facade.Name}");
    }
}
```

**Expected**: No compilation errors, logs show success.

---

## 🔍 Troubleshooting

### Error: "git subtree split failed"

**Cause**: Package folder doesn't exist or wrong path

**Fix**:
```bash
# Verify folder exists
ls "Assets/com.game.addressables"

# Check path in deploy.sh
PREFIX="Assets/com.game.addressables"  # Should match exactly
```

### Error: "tag already exists"

**Cause**: You already deployed this version

**Fix**:
```bash
# Delete old tag
git tag -d 1.0.3
git push origin :refs/tags/1.0.3

# Re-run deploy
./deploy.sh --semver "1.0.3"
```

### Error: "refusing to update checked out branch"

**Cause**: You're on the `upm` branch

**Fix**:
```bash
# Switch to main
git checkout main

# Re-run deploy
./deploy.sh --semver "1.0.3"
```

### Unity Can't Find Package

**Cause**: GitHub URL might be wrong or tag doesn't exist

**Fix**:
```bash
# Verify tags exist on GitHub
git ls-remote --tags origin

# Check URL format
https://github.com/YOUR_USERNAME/addressable-manager.git#1.0.3
                   ^^^^^^^^^^^^^^ ^^^^^^^^^^^^^^^^^^       ^^^^^
                   Your username  Repository name          Tag
```

---

## 📝 Best Practices

### DO's
- ✅ Always update package.json version before deploying
- ✅ Always update CHANGELOG.md
- ✅ Commit to main before running deploy script
- ✅ Test in a clean Unity project after deploying
- ✅ Use semantic versioning (1.0.3, 1.0.4, 1.1.0, 2.0.0)
- ✅ Keep deploy.sh in repository root
- ✅ Create GitHub Releases for major versions (optional but nice)

### DON'Ts
- ❌ Don't deploy without committing to main first
- ❌ Don't reuse version numbers (no re-deploying 1.0.3)
- ❌ Don't manually edit the `upm` branch
- ❌ Don't change package.json name after first release
- ❌ Don't delete tags (breaks existing installs)

---

## 🎯 Quick Reference

### Deploy Checklist

```bash
# 1. Update package.json version
# 2. Update CHANGELOG.md
# 3. Commit changes
git add .
git commit -m "Release v1.0.X"
git push origin main

# 4. Deploy
./deploy.sh --semver "1.0.X"

# 5. Verify on GitHub
# 6. Test in clean Unity project
```

### Common Commands

```bash
# Deploy new version
./deploy.sh --semver "1.0.4"

# View all tags
git tag -l

# Delete tag (if needed)
git tag -d 1.0.3
git push origin :refs/tags/1.0.3

# View remote tags
git ls-remote --tags origin
```

---

## 🌟 Comparison: Git Subtree vs Basic Git URL

### Basic Git URL Method

**Installation**:
```json
"com.game.addressables": "https://github.com/user/repo.git"
```

**Installs**:
- Entire repository (Unity project + package)
- Large download size
- Slower installation

**Best for**:
- Quick prototypes
- Internal packages
- Single-developer projects

### Git Subtree Method (This Guide)

**Installation**:
```json
"com.game.addressables": "https://github.com/user/repo.git#1.0.3"
```

**Installs**:
- Package files only
- Small download size
- Fast installation

**Best for**:
- Production packages
- Public distribution
- Professional releases
- Team collaboration

---

## 📚 Additional Resources

### Git Subtree
- [Git Subtree Documentation](https://git-scm.com/docs/git-subtree)
- [Atlassian Git Subtree Tutorial](https://www.atlassian.com/git/tutorials/git-subtree)

### Unity Package Manager
- [UPM Documentation](https://docs.unity3d.com/Manual/upm-ui.html)
- [Custom Packages](https://docs.unity3d.com/Manual/CustomPackages.html)
- [Package Manifest](https://docs.unity3d.com/Manual/upm-manifestPkg.html)

### Semantic Versioning
- [SemVer Specification](https://semver.org/)

---

## ✅ Final Deployment Checklist

Before running `./deploy.sh`:

- [ ] All code changes committed to main branch
- [ ] Version updated in package.json
- [ ] CHANGELOG.md updated with release notes
- [ ] All bugs fixed and tested
- [ ] No compilation errors
- [ ] Documentation updated
- [ ] Main branch pushed to GitHub
- [ ] deploy.sh is executable (`chmod +x deploy.sh`)

After running `./deploy.sh`:

- [ ] Check GitHub for new tag
- [ ] Verify tag contains only package files
- [ ] Test installation in clean Unity project
- [ ] Update project README with new version
- [ ] (Optional) Create GitHub Release

---

## 🎉 You're Ready to Deploy!

Your deployment process is now professional-grade:

1. **Clean UPM branch** - Only package files
2. **Version tags** - Easy to install specific versions
3. **Fast installs** - Small download size
4. **Automated** - One command deployment
5. **Production-ready** - Used by major Unity packages

**Deploy your first version**:
```bash
./deploy.sh --semver "1.0.3"
```

Good luck! 🚀

---

*Last Updated: January 2025*
*Addressable Manager v1.0.3*
*Professional Git Subtree Deployment*
