#!/bin/sh -ex

# Addressable Manager - UPM Deployment Script
# This script creates a clean UPM branch with only the package contents
# Usage: ./deploy.sh --semver "1.0.3"

# Parse arguments
while [ $# -gt 0 ]; do
  case "$1" in
    --semver=*)
      SEMVER="${1#*=}"
      ;;
    --semver)
      SEMVER="$2"
      shift
      ;;
    *)
      echo "Usage: ./deploy.sh --semver \"1.0.3\""
      exit 1
      ;;
  esac
  shift
done

# Validate semver argument
if [ -z "$SEMVER" ]; then
  echo "Error: --semver argument is required"
  echo "Usage: ./deploy.sh --semver \"1.0.3\""
  exit 1
fi

# Configuration
PREFIX="Packages/com.game.addressables"
BRANCH="upm"

echo "================================"
echo "Deploying Addressable Manager"
echo "Version: $SEMVER"
echo "Prefix: $PREFIX"
echo "Branch: $BRANCH"
echo "================================"

# Step 1: Split the package folder into a separate branch
echo "Step 1/5: Splitting package from main branch..."
git subtree split --prefix="$PREFIX" --branch $BRANCH

# Step 2: Tag the version on the UPM branch
echo "Step 2/5: Creating tag $SEMVER..."
git tag $SEMVER $BRANCH

# Step 3: Push the UPM branch and the new tag to remote
#
# Only this tag, not --tags. Pushing every local tag makes one deploy publish whatever
# else happens to be lying around locally.
#
# If this fails, stop here. An earlier version carried on to steps 4 and 5 and then
# printed "Deployment Complete" regardless: a transient DNS failure produced a run that
# reported success, printed an install URL, and deleted the local branch, while nothing
# had reached the remote. The tag survived only by luck.
echo "Step 3/5: Pushing to origin..."
if ! git push origin "$BRANCH" "refs/tags/$SEMVER"; then
  echo ""
  echo "================================"
  echo "❌ Push FAILED. Nothing was published."
  echo ""
  echo "Local state is intact and the deploy can be retried:"
  echo "  branch '$BRANCH' and tag '$SEMVER' both still exist locally."
  echo ""
  echo "Retry the push alone once the cause is fixed:"
  echo "  git push origin $BRANCH refs/tags/$SEMVER"
  echo ""
  echo "Or delete both and re-run this script:"
  echo "  git tag -d $SEMVER && git branch -D $BRANCH"
  echo "================================"
  exit 1
fi

# Step 4: Clean up remote branch (keeps tags)
echo "Step 4/5: Cleaning up remote branch..."
git push origin --delete $BRANCH || true

# Step 5: Clean up local branch
#
# Safe only because step 3 succeeded: the tag is on the remote, so the commits stay
# reachable there even though the local branch goes away.
echo "Step 5/5: Cleaning up local branch..."
git branch -D $BRANCH

# Confirm the tag is actually on the remote before claiming success. The push above
# reported it, but this is the independent check that costs one round trip.
echo "Verifying the tag on origin..."
if ! git ls-remote --exit-code --tags origin "refs/tags/$SEMVER" > /dev/null 2>&1; then
  echo "❌ Tag '$SEMVER' is NOT on origin despite the push reporting success."
  echo "   Do not hand out the install URL. Investigate before retrying."
  exit 1
fi

echo "================================"
echo "✅ Deployment Complete! (tag verified on origin)"
echo ""
echo "Installation URL for users:"
echo "https://github.com/TeamAcMong/Addressable-System.git#$SEMVER"
echo ""
echo "Or in manifest.json:"
echo "{"
echo "  \"dependencies\": {"
echo "    \"com.game.addressables\": \"https://github.com/TeamAcMong/Addressable-System.git#$SEMVER\""
echo "  }"
echo "}"
echo "================================"
