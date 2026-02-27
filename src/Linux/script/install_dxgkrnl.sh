#!/bin/bash -e
# install_dxgkrnl.sh
WORKDIR="$(dirname $(realpath $0))"
LINUX_DISTRO="$(cat /etc/*-release)"
LINUX_DISTRO=${LINUX_DISTRO,,}

PATCH_BASE_URL="https://raw.githubusercontent.com/Justsenger/ExHyperV/main/src/Linux/script/patches"

KERNEL_6_6_NEWER_REGEX="^(6\.[6-9]\.|6\.[0-9]{2,}\.)"
KERNEL_5_15_NEWER_REGEX="^(5\.1[5-9]+\.|6\.[0-5]\.)"

install_dependencies() {
    NEED_TO_INSTALL=""
    if [ ! -e "/bin/git" ] && [ ! -e "/usr/bin/git" ]; then
        NEED_TO_INSTALL="git"; 
    fi
    if [ ! -e "/usr/bin/curl" ] && [ ! -e "/bin/curl" ]; then
        NEED_TO_INSTALL="$NEED_TO_INSTALL curl"
    fi
    if [ ! -e "/sbin/dkms" ] && [ ! -e "/bin/dkms" ] && [ ! -e "/usr/bin/dkms" ]; then
        NEED_TO_INSTALL="$NEED_TO_INSTALL dkms"
    fi

    if [[ ! -z "$NEED_TO_INSTALL" ]]; then
        echo "Installing basic dependencies: $NEED_TO_INSTALL"
        if [[ "$LINUX_DISTRO" == *"debian"* || "$LINUX_DISTRO" == *"ubuntu"* ]]; then
            apt update;
            apt install -y $NEED_TO_INSTALL;
        elif [[ "$LINUX_DISTRO" == *"fedora"* ]]; then
            yum -y install $NEED_TO_INSTALL;
        elif [[ "$LINUX_DISTRO" == *"arch"* ]]; then
            pacman -Sy --noconfirm $NEED_TO_INSTALL;
        else
            echo "Fatal: The system distro is unsupported";
            exit 1;
        fi
    else
        echo "Basic dependencies (git, curl, dkms) are already installed."
    fi
}

check_and_install_kernel() {
    # (此函数无需修改，保留你原有的完整逻辑即可)
    echo ""
    echo "========================================"
    echo "  Checking Kernel Headers"
    echo "========================================"
    echo "Target kernel version: ${TARGET_KERNEL_VERSION}"
    
    if [[ "$LINUX_DISTRO" == *"arch"* ]]; then
        if [ -e "/usr/lib/modules/${TARGET_KERNEL_VERSION}/build" ]; then
            echo "✓ Kernel headers found for ${TARGET_KERNEL_VERSION}"
            return 0
        fi
    else
        if [ -e "/usr/src/linux-headers-${TARGET_KERNEL_VERSION}" ] && [ -e "/lib/modules/${TARGET_KERNEL_VERSION}/build" ]; then
            echo "✓ Kernel headers found for ${TARGET_KERNEL_VERSION}"
            return 0
        fi
    fi
    
    echo "✗ Kernel headers not found for ${TARGET_KERNEL_VERSION}"
    echo ""
    echo "Will install a new kernel from standard repository..."
    echo ""
    
    if [[ "$LINUX_DISTRO" == *"debian"* || "$LINUX_DISTRO" == *"ubuntu"* ]]; then
        apt update
        AVAILABLE_KERNELS=$(apt-cache search "^linux-image-[0-9]" | \
            awk '{print $1}' | \
            grep -E "^linux-image-[0-9]+\.[0-9]+\.[0-9]+-[0-9]+-(amd64|generic)$" | \
            grep -v -- "-unsigned" | \
            grep -v -- "-dbg" | \
            grep -v -- "-cloud" | \
            sort -V | tail -5)
        
        if [ -z "$AVAILABLE_KERNELS" ]; then
            echo "ERROR: No standard kernel images found in repository!"
            exit 1
        fi
        
        NEW_KERNEL_IMAGE=$(echo "$AVAILABLE_KERNELS" | tail -1)
        NEW_KERNEL_VERSION=$(echo "$NEW_KERNEL_IMAGE" | sed 's/linux-image-//')
        NEW_KERNEL_HEADERS="linux-headers-${NEW_KERNEL_VERSION}"
        
        if ! apt-cache show "$NEW_KERNEL_IMAGE" >/dev/null 2>&1 || ! apt-cache show "$NEW_KERNEL_HEADERS" >/dev/null 2>&1; then
            echo "ERROR: Kernel packages not found!"
            exit 1
        fi
        
        if dpkg -l | grep -q "^ii.*$NEW_KERNEL_IMAGE\s"; then
            echo "✓ Kernel image $NEW_KERNEL_VERSION is already installed"
        else
            echo "Installing kernel image and headers..."
            apt install -y "$NEW_KERNEL_IMAGE" "$NEW_KERNEL_HEADERS"
            echo "STATUS: REBOOT_REQUIRED"
            exit 0
        fi
        
        if ! dpkg -l | grep -q "^ii.*$NEW_KERNEL_HEADERS\s"; then
            apt install -y "$NEW_KERNEL_HEADERS"
        fi
        
        TARGET_KERNEL_VERSION="$NEW_KERNEL_VERSION"
        
    elif [[ "$LINUX_DISTRO" == *"fedora"* ]]; then
        yum -y install kernel kernel-devel
        NEW_KERNEL_VERSION=$(rpm -q kernel --last | head -1 | awk '{print $1}' | sed 's/kernel-//')
        echo "STATUS: REBOOT_REQUIRED"
        exit 0
        
    elif [[ "$LINUX_DISTRO" == *"arch"* ]]; then
        HEADERS_PKG="linux-headers"
        [[ "$TARGET_KERNEL_VERSION" == *"-lts66"* ]] && HEADERS_PKG="linux-lts66-headers"
        [[ "$TARGET_KERNEL_VERSION" == *"-lts"* ]] && HEADERS_PKG="linux-lts-headers"
        [[ "$TARGET_KERNEL_VERSION" == *"-zen"* ]] && HEADERS_PKG="linux-zen-headers"
        [[ "$TARGET_KERNEL_VERSION" == *"-hardened"* ]] && HEADERS_PKG="linux-hardened-headers"
        
        pacman -Sy --noconfirm "$HEADERS_PKG" || true
        if [ ! -e "/usr/lib/modules/${TARGET_KERNEL_VERSION}/build" ]; then
            echo "ERROR: Headers not found for ${TARGET_KERNEL_VERSION}"
            exit 1
        fi
    else
        echo "Fatal: The system distro is unsupported"; exit 1;
    fi
}

update_git() {
    KERNEL_MAJOR=$(echo ${TARGET_KERNEL_VERSION} | grep -oP '^\d+')
    KERNEL_MINOR=$(echo ${TARGET_KERNEL_VERSION} | grep -oP '^\d+\.(\d+)' | grep -oP '\d+$')
    
    echo ""
    echo "========================================"
    echo "  Preparing Source Code"
    echo "========================================"
    
    if [[ "$KERNEL_MAJOR" -eq 6 && "$KERNEL_MINOR" -ge 6 ]] || [[ "$KERNEL_MAJOR" -gt 6 ]]; then
        TARGET_BRANCH="linux-msft-wsl-6.6.y"
    elif [[ "$KERNEL_MAJOR" -eq 5 && "$KERNEL_MINOR" -ge 15 ]] || [[ "$KERNEL_MAJOR" -eq 6 && "$KERNEL_MINOR" -lt 6 ]]; then
        TARGET_BRANCH="linux-msft-wsl-5.15.y"
    else
        echo "Fatal: Unsupported kernel version ${TARGET_KERNEL_VERSION}"; exit 1
    fi

    if [ ! -e "/tmp/WSL2-Linux-Kernel" ]; then
        echo "Cloning WSL2-Linux-Kernel repository (Optimized)..."
        # 【修改点 1】: 添加 --filter=blob:none 开启无 Blob 过滤克隆，极大提升拉取速度，防止 SSH 触发 Timeout。
        git clone --filter=blob:none --no-checkout --depth=1 --sparse --branch=$TARGET_BRANCH https://github.com/microsoft/WSL2-Linux-Kernel.git /tmp/WSL2-Linux-Kernel
    fi

    cd /tmp/WSL2-Linux-Kernel;

    if [ "`git branch -a | grep -o $TARGET_BRANCH`" == "" ]; then
        # 【修改点 2】: 同样为 fetch 添加 filter 提升速度
        git fetch --filter=blob:none --depth=1 origin $TARGET_BRANCH:$TARGET_BRANCH;
    fi

    git sparse-checkout set --no-cone /drivers/hv/dxgkrnl /include/uapi/misc/d3dkmthk.h
    git checkout -f $TARGET_BRANCH
}

get_version() {
    cd /tmp/WSL2-Linux-Kernel
    CURRENT_BRANCH=$(git rev-parse --abbrev-ref HEAD)
    VERSION=$(git rev-parse --short HEAD)
}

install() {
    # (此函数无需修改，保留你原有的完整逻辑即可)
    echo ""
    echo "========================================"
    echo "  Applying Patches"
    echo "========================================"
    cd /tmp/WSL2-Linux-Kernel

    case $CURRENT_BRANCH in
        "linux-msft-wsl-5.15.y")
            PATCHES="0001-Add-a-gpu-pv-support.patch 0002-Add-a-multiple-kernel-version-support.patch";
            if [[ "$TARGET_KERNEL_VERSION" != *"azure"* ]]; then
                    PATCHES="$PATCHES 0003-Fix-gpadl-has-incomplete-type-error.patch";
            fi
            for PATCH in $PATCHES; do
                curl -fsSL "$PATCH_BASE_URL/linux-msft-wsl-5.15.y/$PATCH" | git apply -v;
            done
            ;;
        "linux-msft-wsl-6.6.y")
            curl -fsSL "$PATCH_BASE_URL/linux-msft-wsl-5.15.y/0001-Add-a-gpu-pv-support.patch" | git apply -v;
            PATCHES="";
            if [[ "$TARGET_KERNEL_VERSION" != *"truenas"* ]]; then
                PATCHES="0002-Fix-eventfd_signal.patch";
            fi
            for PATCH in $PATCHES; do
                curl -fsSL "$PATCH_BASE_URL/linux-msft-wsl-6.6.y/$PATCH" | git apply -v --ignore-whitespace --ignore-space-change;
            done
            ;;
        *)
            exit 1;;
    esac

    echo "Copying dxgkrnl driver..."
    cp -r ./drivers/hv/dxgkrnl /usr/src/dxgkrnl-$VERSION
    cp -r ./include /usr/src/dxgkrnl-$VERSION/include

    DXGMODULE_FILE="/usr/src/dxgkrnl-$VERSION/dxgmodule.c"
    if [ -f "$DXGMODULE_FILE" ] && grep -q "eventfd_signal(event->cpu_event);" "$DXGMODULE_FILE"; then
        NEEDS_TWO_PARAMS=false
        if grep -q "eventfd_signal.*struct eventfd_ctx.*__u64" /usr/lib/modules/${TARGET_KERNEL_VERSION}/build/include/linux/eventfd.h 2>/dev/null; then
            NEEDS_TWO_PARAMS=true
        elif grep -q "eventfd_signal.*struct eventfd_ctx.*__u64" /lib/modules/${TARGET_KERNEL_VERSION}/build/include/linux/eventfd.h 2>/dev/null; then
            NEEDS_TWO_PARAMS=true
        fi
        if [ "$NEEDS_TWO_PARAMS" = true ]; then
            sed -i 's/eventfd_signal(event->cpu_event);/eventfd_signal(event->cpu_event, 1);/g' "$DXGMODULE_FILE"
        fi
    fi

    sed -i 's/\$(CONFIG_DXGKRNL)/m/' /usr/src/dxgkrnl-$VERSION/Makefile
    echo "EXTRA_CFLAGS=-I\$(PWD)/include -D_MAIN_KERNEL_" >> /usr/src/dxgkrnl-$VERSION/Makefile

    if [[ "$CURRENT_BRANCH" == "linux-msft-wsl-6.6.y" ]]; then
        BUILD_EXCLUSIVE_KERNEL=$KERNEL_6_6_NEWER_REGEX
    else
        BUILD_EXCLUSIVE_KERNEL=$KERNEL_5_15_NEWER_REGEX
    fi

    cat > /usr/src/dxgkrnl-$VERSION/dkms.conf << EOF
PACKAGE_NAME="dxgkrnl"
PACKAGE_VERSION="$VERSION"
BUILT_MODULE_NAME="dxgkrnl"
DEST_MODULE_LOCATION="/kernel/drivers/hv/dxgkrnl/"
AUTOINSTALL="yes"
BUILD_EXCLUSIVE_KERNEL="$BUILD_EXCLUSIVE_KERNEL"
EOF
}

install_dkms() {
    echo ""
    echo "========================================"
    echo "  Building and Installing DKMS Module"
    echo "========================================"
    
    if [[ "$LINUX_DISTRO" == *"arch"* ]]; then
        HEADERS_PATH="/usr/lib/modules/${TARGET_KERNEL_VERSION}/build"
    else
        HEADERS_PATH="/lib/modules/${TARGET_KERNEL_VERSION}/build"
    fi
    
    if dkms status | grep -q "dxgkrnl/$VERSION"; then
        dkms remove dxgkrnl/$VERSION --all
    fi
    
    dkms -k ${TARGET_KERNEL_VERSION} add dxgkrnl/$VERSION
    
    if ! dkms -k ${TARGET_KERNEL_VERSION} build dxgkrnl/$VERSION; then
        echo "DKMS build failed. Please check the build log."
        exit 1
    fi
    
    dkms -k ${TARGET_KERNEL_VERSION} install dxgkrnl/$VERSION --force || echo "Notice: DKMS install reported an error. This is often a signing issue on Secure Boot systems and can usually be ignored."

    MODULE_FOUND=false
    SEARCH_LOCS=(
        "/lib/modules/${TARGET_KERNEL_VERSION}/updates/dkms/"
        "/usr/lib/modules/${TARGET_KERNEL_VERSION}/updates/dkms/"
        "/lib/modules/${TARGET_KERNEL_VERSION}/extra/"
    )

    for loc in "${SEARCH_LOCS[@]}"; do
        if ls ${loc}dxgkrnl.ko* 1> /dev/null 2>&1; then
            MODULE_FOUND=true
            FOUND_PATH=$loc
            break
        fi
    # 【修改点 3】: 添加 || true 兜底。
    # 如果系统完全没生成 .ko 文件，循环退出时最后一个执行失败的 ls 会把整个 for 循环的退出码污染为 2。
    # 在 set -e 规则下这会引发瞬间静默崩溃，导致底下的 if 错误处理和最后的 STATUS: SUCCESS 永远执行不到。
    done || true
    
    if [ "$MODULE_FOUND" = true ]; then
        echo ""
        echo "✓ dxgkrnl.ko file confirmed at: $FOUND_PATH"
        echo "✓ DKMS module installed (ignoring minor hook errors)."
    else
        echo ""
        echo "Error: DKMS installation failed. Module file not found in system directories."
        exit 1
    fi

    echo ""
    echo "✓ DKMS module process finished"
}

all() {
    TARGET_KERNEL_VERSION="$1";
    if [ -z "$TARGET_KERNEL_VERSION" ]; then
        TARGET_KERNEL_VERSION=`uname -r`
    fi

    echo "========================================"
    echo "  dxgkrnl Installation Script"
    echo "========================================"
    echo "Initial target kernel: ${TARGET_KERNEL_VERSION}"
    echo "Current running kernel: $(uname -r)"
    echo ""
    
    install_dependencies
    check_and_install_kernel
    update_git
    get_version
    install
    install_dkms
}

if [ -z "$1" ]; then
    all `uname -r`
elif [[ "$1" =~ ^[0-9]+\.[0-9]+\.[0-9]+.+$ ]]; then
    all $1
else
    echo "Usage: $0 [kernel_version]"
    exit 1
fi

echo ""
echo "========================================"
echo "  Installation Completed Successfully!"
echo "========================================"
echo "Kernel version: ${TARGET_KERNEL_VERSION}"
echo "Module installed: dxgkrnl/${VERSION}"
echo ""
echo "STATUS: SUCCESS"