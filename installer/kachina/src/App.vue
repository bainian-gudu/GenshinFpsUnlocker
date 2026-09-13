<template>
  <div class="main">
    <div v-show="init === 0" class="init-loading">
      <span class="fui-Spinner__spinner">
        <span class="fui-Spinner__spinnerTail"></span>
      </span>
    </div>
    <div
      v-show="init === 2 && !dialog"
      class="content"
      :class="{ borderless: PROJECT_CONFIG.windowBorderless }"
    >
      <div class="controls" v-if="PROJECT_CONFIG.windowBorderless">
        <button class="cont-minimize" @click="minimize">
          <IconMinimize />
        </button>
        <button class="cont-close" @click="close">
          <IconClose />
        </button>
      </div>
      <div class="image">
        <img
          v-if="!useDynamicCss"
          :src="imageSource"
          :alt="PROJECT_CONFIG.title"
        />
      </div>
      <div class="right">
        <div class="title">{{ PROJECT_CONFIG.title }}</div>
        <div class="desc">{{ PROJECT_CONFIG.description }}</div>
        <div v-if="step === 1" class="actions">
          <div v-if="!isUpdate && !INSTALLER_CONFIG.is_uninstall" class="lnk">
            <Checkbox v-model="createLnk" />
            创建桌面快捷方式
          </div>
          <div v-if="!isUpdate && !INSTALLER_CONFIG.is_uninstall" class="read">
            <Checkbox v-model="acceptEula" />
            我已阅读并同意
            <a
              class="agreement-link"
              :class="{ 'agreement-link-off': !hasAgreement }"
              :title="hasAgreement ? `点击查看${agreementTitle}全文` : undefined"
              @click="openAgreement"
            >
              {{ agreementTitle }}
            </a>
          </div>
          <div
            v-if="INSTALLER_CONFIG.is_uninstall"
            class="read"
            title="删除 %LOCALAPPDATA%\GenshinFpsUnlocker（含 config.json、logs、webview2 界面缓存）；本机其它账户的同名数据目录也会一并清理。不勾选则保留，方便日后重装。"
          >
            <Checkbox v-model="deleteUserData" />
            同时删除用户数据（配置、日志与界面缓存）
          </div>
          <div class="more">
            <span>
              <template
                v-if="
                  !INSTALLER_CONFIG.is_uninstall &&
                  Array.isArray(PROJECT_CONFIG.source) &&
                  PROJECT_CONFIG.source.length > 1 &&
                  !INSTALLER_CONFIG.embedded_index?.length
                "
              >
                <span>从 </span>
                <a @click="dialog = 'source'" title="点击切换安装源">
                  {{
                    PROJECT_CONFIG.source.find((e) => e.uri === selectedSource)
                      ?.name
                  }}<template v-if="installMode === 'mirrorc'"
                    >({{ mirrorcKey ? markedKey : '无CDK' }})</template
                  >
                  <IconEdit />
                </a>
              </template>
              <span v-if="!isUpdate && !INSTALLER_CONFIG.is_uninstall">
                安装到
              </span>
              <span v-if="isUpdate && !INSTALLER_CONFIG.is_uninstall">
                更新到
              </span>
              <span v-if="INSTALLER_CONFIG.is_uninstall"> 卸载自 </span>
            </span>
            <a
              v-if="!INSTALLER_CONFIG.is_uninstall"
              @click="changeSource"
              title="点击修改安装路径"
              >{{ source }}<IconEdit
            /></a>
            <a v-else>{{ source }}</a>
          </div>
          <button
            v-if="!INSTALLER_CONFIG.is_uninstall"
            class="btn btn-install"
            @click="install"
            :disabled="!isUpdate && !acceptEula"
          >
            <IconSheild
              style="
                width: 20px;
                margin-right: 6px;
                margin-left: -6px;
                padding-top: 2px;
              "
              v-if="needElevate || INSTALLER_CONFIG.elevated"
            />
            {{ isUpdate ? '更新' : '安装' }}
          </button>
          <button
            v-if="INSTALLER_CONFIG.is_uninstall"
            class="btn btn-install"
            @click="uninstall"
          >
            <IconSheild
              style="
                width: 20px;
                margin-right: 6px;
                margin-left: -6px;
                padding-top: 2px;
              "
              v-if="needElevate || INSTALLER_CONFIG.elevated"
            />
            卸载
          </button>
        </div>
        <div class="progress" v-if="step === 2">
          <div class="step-desc">
            <div
              v-for="(i, a) in installMode === 'mirrorc'
                ? subStepListMirrorc
                : subStepList"
              class="substep"
              :class="{ done: a < subStep }"
              v-show="a <= subStep"
              :key="i"
            >
              <span v-if="a === subStep" class="fui-Spinner__spinner">
                <span class="fui-Spinner__spinnerTail"></span>
              </span>
              <span v-else class="substep-done">
                <CircleSuccess />
              </span>
              <div>{{ i }}</div>
            </div>
          </div>
          <div class="current-status" v-html="current"></div>
          <div class="progress-bar" :style="{ width: `${percent}%` }"></div>
        </div>
        <div class="finish" v-if="step === 3">
          <div class="finish-text">
            <CircleSuccess />
            {{ isUpdate ? '更新' : '安装' }}完成
          </div>
          <button class="btn btn-install" @click="launch">启动</button>
        </div>
        <div class="finish" v-if="step === 4">
          <div class="finish-text">
            <CircleSuccess />
            您已安装最新版本
          </div>
          <button class="btn btn-install" @click="launch">启动</button>
        </div>
        <div class="uninstall" v-if="step === 5">
          <button class="btn btn-install" disabled>
            <span
              class="fui-Spinner__spinner"
              style="width: 16px; height: 16px; margin-right: 8px"
            >
              <span class="fui-Spinner__spinnerTail"></span>
            </span>
            卸载中
          </button>
        </div>
        <div class="finish" v-if="step === 6">
          <div class="finish-text">
            <CircleSuccess />
            卸载成功
          </div>
          <button class="btn btn-install" @click="exit">关闭</button>
        </div>
      </div>
    </div>
    <Dialog v-show="dialog === 'source'" @keydown="handleKeyDown">
      <template #title>
        <div class="title">选择安装源</div>
      </template>
      <template #desc>
        <div class="desc">{{ PROJECT_CONFIG.title }}支持多种在线安装方式。</div>
      </template>
      <template #body v-if="Array.isArray(PROJECT_CONFIG.source)">
        <div class="card-container">
          <template v-for="i in PROJECT_CONFIG.source">
            <div
              class="card"
              v-if="
                !i.hidden ||
                showHiddenSources ||
                INSTALLER_CONFIG.args.source === i.id
              "
              :key="i.id"
              :class="{ active: i.uri === selectedSource }"
              @click="changeSelectedSource(i.uri)"
            >
              <SafeIcon
                :svg-content="i.icon"
                :fallback-component="getDefaultIconComponent(i.uri)"
              />
              <span>{{ i.name }}</span>
            </div>
          </template>
        </div>
      </template>
    </Dialog>
    <Dialog v-show="dialog === 'mirrorc'">
      <template #title><div class="title">设置 Mirror酱 CDK</div></template>
      <template #desc>
        <div class="desc">
          Mirror酱是独立的第三方软件下载平台，提供付费的软件下载加速服务。<br />
          如果你有 Mirror酱的 CDK，可以在这里输入。
        </div>
      </template>
      <template #body>
        <FInput
          class="cdk-input"
          v-model="mirrorcTempKey"
          type="text"
          placeholder="请输入 Mirror酱 CDK"
        />
        <div class="desc">
          <a style="cursor: pointer" @click="openMirrorc">获取 CDK</a>
        </div>
      </template>
      <template #footer>
        <button
          class="btn btn-install btn-install-2rd neutral"
          @click="dialog = ''"
        >
          取消
        </button>
        <button
          class="btn btn-install"
          :disabled="mirrorcChecking"
          @click="changeMirrorcKey"
        >
          <span
            v-if="mirrorcChecking"
            class="fui-Spinner__spinner"
            style="width: 16px; height: 16px; margin-right: 8px"
          >
            <span class="fui-Spinner__spinnerTail"></span>
          </span>
          确定
        </button>
      </template>
    </Dialog>
    <Dialog v-show="dialog === 'agreement'">
      <template #title>
        <div class="title">{{ agreementTitle }}</div>
      </template>
      <template #desc>
        <div class="desc">
          请在使用 {{ PROJECT_CONFIG.title }} 前完整阅读以下条款。
        </div>
      </template>
      <template #body>
        <!-- 正文经 DOMPurify 净化，见 utils/agreement.ts -->
        <div
          class="agreement-body"
          v-html="agreementHtml"
          @click="onAgreementClick"
        ></div>
      </template>
      <template #footer>
        <button
          class="btn btn-install btn-install-2rd neutral"
          @click="closeAgreement(false)"
        >
          关闭
        </button>
        <button class="btn btn-install" @click="closeAgreement(true)">
          我已阅读并同意
        </button>
      </template>
    </Dialog>
    <component :is="'style'" v-if="useDynamicCss">{{ dynamicCss }}</component>
  </div>
</template>

<style scoped>
.main {
  min-height: 100vh;
  app-region: drag;
}
.init-loading {
  height: 100vh;
  display: flex;
  justify-content: center;
  align-items: center;
  padding-bottom: 24px;
  box-sizing: border-box;
}

.init-loading .fui-Spinner__spinner {
  width: 40px;
  height: 40px;
  --fui-Spinner--strokeWidth: 4px;
}
.content {
  display: flex;
  min-height: 100vh;
  line-height: 1.1;
  text-align: center;
  justify-content: center;
  user-select: none;
  padding: 0 16px;
  gap: 8px;
}

.desc {
  font-size: 14px;
  opacity: 0.8;
  padding-left: 10px;
  padding-bottom: 2px;
}

.image {
  min-width: 180px;
  width: 180px;
  box-sizing: border-box;
  padding: 12px 0 12px 12px;

  img {
    width: 100%;
    height: 100%;
    object-fit: contain;
  }
}

.right {
  position: relative;
  width: calc(100% - 188px);
  text-align: left;
  display: flex;
  flex-direction: column;
  padding: 16px;
  box-sizing: border-box;
  overflow: hidden;
  .borderless & {
    padding-top: 44px;
  }
}

.title {
  font-size: 25px;
  padding: 2px 10px 6px;
}

.btn-install {
  app-region: no-drag;
  height: 40px;
  width: 140px;
  position: absolute;
  bottom: 20px;
  right: 8px;
  &.btn-install-2rd {
    right: 158px;
    width: 100px;
  }
}

/* 弹窗 footer 里的按钮：.btn-install 的绝对定位是给主界面右下角用的，
   放进弹窗会浮在正文上面，所以在 footer 里改回文档流并收窄一档。
   弹窗骨架的 flex 布局见 Dialog.vue。 */
.dialog-footer .btn-install,
.dialog-footer .btn-install.btn-install-2rd {
  position: static;
  height: 28px;
  width: auto;
  min-width: 72px;
  padding: 0 14px;
  font-size: 12.5px;
}

.actions {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding-top: 16px;
  app-region: no-drag;
}

.read,
.lnk {
  align-items: center;
  gap: 4px;
  padding-left: 12px;
  font-size: 13px;
  display: flex;

  a {
    cursor: pointer;
  }
}

.agreement-link {
  text-decoration: underline;
  text-underline-offset: 2px;
}

.agreement-link-off {
  cursor: default;
  text-decoration: none;
}

.agreement-body {
  app-region: no-drag;
  /* 高度由弹窗骨架的 flex 分配，自己只管滚动。
     早先写死的 max-height: 46vh 会和 footer 抢地方。 */
  flex: 1 1 auto;
  min-height: 0;
  overflow-y: auto;
  overflow-x: hidden;
  padding: 10px 12px;
  margin: 8px 0 4px;
  font-size: 12.5px;
  line-height: 1.8;
  text-align: left;
  word-break: break-word;
  user-select: text;
  border: 1px solid var(--colorNeutralStroke1, rgba(0, 0, 0, 0.12));
  border-radius: 4px;
  background: var(--colorNeutralBackground2, rgba(0, 0, 0, 0.04));
}

.agreement-body :deep(.agreement-plain) {
  margin: 0;
  white-space: pre-wrap;
  font-family: inherit;
}

.agreement-body :deep(p) {
  margin: 0 0 8px;
}

.agreement-body :deep(h2),
.agreement-body :deep(h3),
.agreement-body :deep(h4),
.agreement-body :deep(h5),
.agreement-body :deep(h6) {
  margin: 12px 0 6px;
  font-size: 13.5px;
  font-weight: 600;
  line-height: 1.5;
}

.agreement-body :deep(ul),
.agreement-body :deep(ol) {
  margin: 0 0 8px;
  padding-left: 20px;
}

.agreement-body :deep(li) {
  margin: 2px 0;
}

.agreement-body :deep(blockquote) {
  margin: 8px 0;
  padding: 4px 10px;
  border-left: 3px solid var(--colorBrandStroke1, #0f6cbd);
}

.agreement-body :deep(pre) {
  margin: 8px 0;
  padding: 8px 10px;
  overflow-x: auto;
  border-radius: 4px;
  background: rgba(0, 0, 0, 0.06);
}

.agreement-body :deep(code) {
  font-family: Consolas, 'Courier New', monospace;
  font-size: 12px;
}

.agreement-body :deep(hr) {
  margin: 10px 0;
  border: 0;
  border-top: 1px solid var(--colorNeutralStroke1, rgba(0, 0, 0, 0.12));
}

.agreement-body :deep(a) {
  color: var(--colorBrandForegroundLink, #0f6cbd);
}

.more {
  align-items: flex-start;
  gap: 6px;
  padding-top: 8px;
  padding-left: 10px;
  font-size: 13px;
  display: flex;
  flex-direction: column;
  svg {
    width: 12px;
    position: relative;
    top: 2px;
    padding-left: 2px;
    opacity: 0.8;
  }

  span {
    span {
      opacity: 0.8;
    }
  }

  a {
    cursor: pointer;
    font-family:
      Consolas,
      'Courier New',
      Microsoft Yahei;
    opacity: 0.8;
    font-size: 12px;
  }
}

.finish-text {
  text-align: center;
  opacity: 0.9;
  width: 100%;
  padding: 38px 10px;
  font-size: 18px;
  display: flex;
  justify-content: center;
  gap: 8px;
  align-items: center;

  svg {
    width: 24px;
  }
}

.progress-bar {
  position: fixed;
  bottom: 0;
  left: 0;
  height: 4px;
  background: var(--colorBrandForeground1);
  transition: width 0.1s;
  transition-timing-function: cubic-bezier(0.33, 0, 0.67, 1); /* 相关实现：easeInOut */
  width: 30%;
}

.step-desc {
  padding: 14px 10px;
  font-size: 14px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.substep {
  display: flex;
  gap: 6px;

  .fui-Spinner__spinner {
    width: 16px;
    height: 16px;
    display: block;
  }

  .substep-done {
    width: 16px;
    height: 16px;
    display: block;
  }
}

.substep.done {
  font-size: 13px;
  opacity: 0.8;
}

.current-status {
  position: relative;
  max-width: 100%;
  font-size: 12px;
  opacity: 0.7;
  padding-left: 14px;
  margin-top: -6px;
  font-family:
    Consolas,
    'Courier New',
    Microsoft Yahei;
}
.uninstall {
  height: 117px;
  display: flex;
  justify-content: center;
  align-items: center;
}

.uninstall .fui-Spinner__spinner {
  width: 40px;
  height: 40px;
  display: block;
  --fui-Spinner--strokeWidth: 4px;
}
</style>
<style>
.d-single-stat {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.d-single-list {
  display: flex;
  flex-direction: column;
  height: 55px;
  overflow: hidden;
  padding-top: 4px;
  font-size: 11px;
  gap: 2px;
  width: 230px;
  max-height: 250px;
  overflow-y: auto;
  padding-left: 20px;

  &::-webkit-scrollbar {
    width: 4px;
  }

  &::-webkit-scrollbar-thumb {
    background: var(--colorBrandForeground1);
    border-radius: 4px;
  }

  &::-webkit-scrollbar-track {
    background: var(--colorBrandBackground);
  }

  &::-webkit-scrollbar-thumb:hover {
    background: var(--colorBrandForeground2);
  }
}

.d-single {
  display: flex;
  justify-content: space-between;
  gap: 8px;
}

.d-single-filename {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
}

.d-single-progress {
  width: 36px;
  min-width: 36px;
}
.cdk-input {
  app-region: no-drag;
  margin: 30px 10px;
  margin-bottom: 48px;
  width: 320px;
  input {
    font-family: Consolas, monospace !important;
  }
}
.card {
  padding: 8px 10px;
  font-size: 12px;
  opacity: 0.6;
  border: 1px solid #fff;
  border-radius: 5px;
  width: 74px;
  height: 74px;
  text-align: center;
  display: flex;
  flex-direction: column;
  justify-content: space-evenly;
  align-items: center;
  cursor: pointer;
  transition: all 0.1s ease-in-out;
  &:hover {
    opacity: 1;
  }
  &.active {
    background: rgba(255, 255, 255, 0.1);
    opacity: 1;
  }
}

.card-container {
  padding: 8px 10px;
  display: flex;
  gap: 18px;
  justify-content: center;
  align-items: center;
  height: 150px;
  app-region: no-drag;
}

.card svg {
  width: 40px;
}

.controls {
  app-region: no-drag;
  position: absolute;
  right: 0;
  top: 0;
  z-index: 9999;
  height: 32px;
  display: flex;
  & > button {
    width: 45px;
    height: 32px;
    overflow: hidden;
    display: flex;
    align-items: center;
    justify-content: center;
    cursor: pointer;
    svg {
      height: 14px;
    }
    appearance: none;
    background: transparent;
    border: 0;
    color: inherit;
    &:active {
      opacity: 0.8;
    }
  }
  .cont-close:hover {
    background: #c42b1c;
  }

  .cont-minimize:hover {
    background: rgba(255, 255, 255, 0.07);
  }
}
</style>
<script lang="ts" setup>
import { computed, onMounted, onUnmounted, reactive, ref, watch } from 'vue';
import Checkbox from './Checkbox.vue';
import CircleSuccess from './CircleSuccess.vue';
import IconEdit from './IconEdit.vue';
import { getCurrentWindow, invoke, sep } from './tauri';
import {
  getDfsMetadata,
  cleanupAllDfs2Sessions,
  collectDfs2Ranges,
  dfsIndexCache,
  createDfs2Session,
  preprocessFiles,
  getFileInstallMode,
} from './dfs';
import { pluginManager } from './plugins';
import { networkInsights } from './networkInsights';
import {
  DownloadTaskManager,
  SingleFileTask,
  LocalFileTask,
  MergedGroupTask,
  type DownloadContext,
} from './downloadTaskManager';
import {
  error,
  ipcCheckLocalFiles,
  ipcCreateLnk,
  ipcCreateUninstaller,
  ipcFindProcessByName,
  ipcInstallRuntime,
  ipcIsFolderEmpty,
  ipcKillProcess,
  ipcRmList,
  ipcRunMirrorcDownload,
  ipcRunMirrorcInstall,
  ipcRunUninstall,
  ipcWriteRegistry,
  ipPrepare,
  log,
  MirrorcUpdate,
  warn,
} from './api/ipc';
import IconSheild from './IconSheild.vue';
import { getRuntimeName } from './consts';
import Dialog from './Dialog.vue';
import Cloud from './Cloud.vue';
import CloudPaid from './CloudPaid.vue';
import Feedback from './Feedback.vue';
import SafeIcon from './components/SafeIcon.vue';
import FInput from './FInput.vue';
import { compare } from 'compare-versions';
import { processMirrorcError } from './mirrorc-errors';
import { hasAgreementContent, renderAgreement } from './utils/agreement';
import {
  DfsMetadataHashInfo,
  DfsMetadataHashType,
  DfsUpdateTask,
  InstallerConfig,
  InstallStat,
  InvokeGetDfsMetadataRes,
  InvokeGetDirsRes,
  InvokeSelectDirRes,
  ProjectConfig,
  VirtualMergedFile,
} from './types.ts';
import IconMinimize from './IconMinimize.vue';
import IconClose from './IconClose.vue';

const init = ref(0);

const subStepList: ReadonlyArray<string> = [
  '获取最新版本',
  '校验更新内容',
  '下载和解压文件',
  '准备运行环境',
];
const subStepListMirrorc: ReadonlyArray<string> = [
  '从 Mirror酱 获取最新版本',
  '下载数据包',
  '解压文件',
  '准备运行环境',
];

const isUpdate = ref<boolean>(false);
const acceptEula = ref<boolean>(true);
const createLnk = ref<boolean>(true);
const deleteUserData = ref<boolean>(false);
const step = ref<number>(1);
const subStep = ref<number>(0);
const needElevate = ref(true);

const current = ref<string>('');
const percent = ref<number>(0);
const source = ref<string>('');
const progressInterval = ref<number>(0);

const dialog = ref<'' | 'mirrorc' | 'source' | 'agreement'>('');

// 动态图片/CSS 状态
const imageSource = ref<string>('');
const dynamicCss = ref<string>('');
const useDynamicCss = ref<boolean>(false);

// 隐藏来源彩蛋状态
const commaCount = ref<number>(0);
const showHiddenSources = ref<boolean>(false);
const commaTimeout = ref<number>(0);

const selectedSource = ref<string>('');
const installMode = computed<'default' | 'mirrorc'>(() => {
  if (selectedSource.value.startsWith('mirrorc://')) {
    return 'mirrorc';
  } else {
    return 'default';
  }
});
const mirrorcKey = ref<string>('');
const markedKey = computed(() => {
  return (
    mirrorcKey.value.substring(0, 4) +
    '****' +
    mirrorcKey.value.substring(mirrorcKey.value.length - 4)
  );
});

const getDefaultIconComponent = (uri: string) => {
  if (uri.includes('=beta')) return Feedback;
  if (uri.startsWith('mirrorc://')) return CloudPaid;
  return Cloud;
};
watch(
  () => installMode.value,
  async (newValue) => {
    if (newValue === 'mirrorc' && !mirrorcKey.value) {
      try {
        mirrorcKey.value = await invoke('wincred_read', {
          target: `KachinaInstaller_MirrorChyanCDK_${PROJECT_CONFIG.appName}`,
        });
      } catch (e) {}
    }
  },
);

// 监听对话框状态变化，以重置隐藏来源状态
watch(
  () => dialog.value,
  (newValue, oldValue) => {
    // 离开来源对话框时重置隐藏来源状态
    if (oldValue === 'source' && newValue !== 'source') {
      resetHiddenSourcesState();
    }
  },
);

const PROJECT_CONFIG: ProjectConfig = reactive({
  source: '',
  appName: 'Kachina',
  publisher: 'YuehaiTeam',
  regName: 'Kachina',
  exeName: 'inst.exe',
  uninstallName: 'uninst.exe',
  updaterName: 'update.exe',
  programFilesPath: 'Kachina',
  userDataPath: [],
  ignoreFolderPath: [],
  extraUninstallPath: [],
  title: 'Title',
  description: 'description',
  windowTitle: ' ',
  uacStrategy: 'prefer-admin',
  windowBorderless: false,
});

const INSTALLER_CONFIG: InstallerConfig = reactive({
  install_path: '',
  install_path_exists: false,
  install_path_source: 'DEFAULT',
  is_uninstall: false,
  embedded_config: null,
  enbedded_metadata: null,
  embedded_image: null,
  embedded_files: [],
  embedded_index: [],
  exe_path: '',
  args: {
    target: null,
    uninstall: false,
    non_interactive: false,
    silent: false,
    online: false,
  },
  elevated: false,
});

// 用户协议：正文在打包时由配置项 agreementFile 内联进 embedded_config
// （见 src-tauri/src/builder/pack.rs 的 resolve_agreement），支持
// 文本 / markdown / html 三种格式，点击链接弹窗展示全文。
const agreementTitle = computed<string>(
  () => PROJECT_CONFIG.agreement?.title?.trim() || '用户协议',
);
const hasAgreement = computed<boolean>(() =>
  hasAgreementContent(PROJECT_CONFIG.agreement),
);
const agreementHtml = computed<string>(() =>
  renderAgreement(PROJECT_CONFIG.agreement),
);
function openAgreement() {
  // 未配置协议内容时，链接保持为纯文字（与旧版本行为一致）
  if (!hasAgreement.value) {
    return;
  }
  dialog.value = 'agreement';
}
// 「我已阅读并同意」→ 顺手勾上 EULA；「关闭」→ 只关弹窗，不改勾选状态
function closeAgreement(accepted: boolean) {
  if (accepted) {
    acceptEula.value = true;
  }
  dialog.value = '';
}

/**
 * 拦截协议正文里的所有链接点击。
 *
 * 安装器窗口一旦被导航走，安装/卸载流程就直接断了；因此这里 preventDefault，
 * 只把 http(s) 外链交给系统浏览器（与上游「获取 CDK」使用同一个启动命令），
 * 其余（页内锚点、以及万一漏网的 javascript: 之类）什么都不做。
 */
function onAgreementClick(event: MouseEvent) {
  const target = event.target as HTMLElement | null;
  const anchor = target?.closest?.('a');
  if (!anchor) {
    return;
  }
  event.preventDefault();
  const href = anchor.getAttribute('href') ?? '';
  if (!/^https?:\/\//i.test(href)) {
    return;
  }
  // 与上游「获取 CDK」同一个Command；失败只记日志，不打断安装流程
  invoke('launch', { path: href }).catch((e) => warn('打开协议链接失败:', e));
}

async function getSource(scan: boolean): Promise<InstallerConfig> {
  return await invoke<InstallerConfig>('get_installer_config', {
    scanExe: scan,
  });
}

async function installPrepare(version: string): Promise<boolean> {
  await ipPrepare(needElevate.value);
  // 上游在这里打点上报（sendInsight + buildEventString），本项目已移除遥测：
  // 版本信息只写本地日志，方便排查装的是哪个版本。
  log('installPrepare version', version);
  const target_exe_path = `${source.value}${sep()}${PROJECT_CONFIG.exeName}`;
  const runningExes =
    (await ipcFindProcessByName(PROJECT_CONFIG.exeName).catch(log)) || [];
  if (
    runningExes.find(
      (e) =>
        e[1].toLowerCase().replace(/\\/g, '/') ===
        target_exe_path.toLowerCase().replace(/\\/g, '/'),
    )
  ) {
    const result =
      INSTALLER_CONFIG.args.non_interactive ||
      INSTALLER_CONFIG.args.silent ||
      (await confirm(
        `检测到${PROJECT_CONFIG.appName}正在运行，是否结束进程并继续安装？`,
        '提示',
      ));
    if (!result) {
      step.value = 1;
      return false;
    } else {
      try {
        try {
          await Promise.all(
            runningExes.map((e) => ipcKillProcess(e[0], needElevate.value)),
          );
        } catch (e) {
          await Promise.all(runningExes.map((e) => ipcKillProcess(e[0], true)));
        }
        return true;
      } catch (e) {
        error(e);
        await dialog_error(`结束进程失败: ${e}`, '出错了');
        step.value = 1;
        return false;
      }
    }
  }
  return false;
}

async function installRuntimes() {
  if (PROJECT_CONFIG.runtimes) {
    log('latest_meta.runtimes', PROJECT_CONFIG.runtimes);
    subStep.value = 3;
    current.value = '安装运行库……';
    for (const tag of PROJECT_CONFIG.runtimes) {
      log(`Installing runtime: ${tag}`);
      current.value = `安装${getRuntimeName(tag)}……`;
      const tryTimes = 3;
      const embedRuntime = INSTALLER_CONFIG.embedded_files?.find(
        (e) => e.name === tag,
      );
      for (let i = 0; i < tryTimes; i++) {
        try {
          await ipcInstallRuntime(
            tag,
            embedRuntime?.offset,
            embedRuntime?.size,
            ({ payload }) => {
              const currentSize = formatSize(payload[0]);
              const targetSize = payload[1] ? formatSize(payload[1]) : '';
              if (payload[0] >= payload[1] - 1) {
                current.value = `安装 ${getRuntimeName(tag)} ……`;
              } else {
                current.value = `下载 ${getRuntimeName(tag)} ……<br>${currentSize}${targetSize ? ` / ${targetSize}` : ''}`;
              }
            },
            needElevate.value,
          );
          break;
        } catch (e) {
          if (i === tryTimes - 1) {
            error(e);
            await dialog_error(
              `安装${getRuntimeName(tag)}失败: ${e}，请手动安装`,
              '出错了',
            );
            break;
          } else {
            log(`安装${getRuntimeName(tag)}失败: ${e}，重试中`);
          }
        }
      }
    }
  }
}

async function runInstall(): Promise<void> {
  step.value = 2;
  let latest_meta = INSTALLER_CONFIG.enbedded_metadata;
  let online_meta: InvokeGetDfsMetadataRes | null = null;
  let online_meta_err = '';
  try {
    online_meta = await getDfsMetadata(
      selectedSource.value,
      INSTALLER_CONFIG.args.dfs_extras,
    );
  } catch (e) {
    online_meta_err = error(e);
  }
  let meta_tag = '';
  if (!latest_meta && !online_meta) {
    await dialog_error(
      '获取更新信息失败，请检查网络连接' +
        (online_meta_err ? `\n${online_meta_err}` : '：未知错误，请检查日志'),
      '出错了',
    );
    step.value = 1;
    return;
  } else if (!latest_meta) {
    latest_meta = online_meta;
    log('Local meta not found, use online meta');
  } else if (
    online_meta &&
    online_meta.tag_name !== latest_meta.tag_name &&
    compare(online_meta.tag_name, latest_meta.tag_name, '>')
  ) {
    log('Version update detected');
    if (
      !INSTALLER_CONFIG.args.non_interactive &&
      !INSTALLER_CONFIG.args.silent &&
      ((isUpdate.value &&
        (INSTALLER_CONFIG.embedded_index?.length || 0) <= 0) ||
        (await confirm('当前安装包不是最新版本，是否直接安装最新版本？')))
    ) {
      meta_tag = latest_meta.tag_name;
      latest_meta = online_meta;
    } else {
      log('Has version update but use local meta');
    }
  } else {
    log('Local meta found, use local meta');
  }
  latest_meta = latest_meta as InvokeGetDfsMetadataRes;
  if (
    isUpdate.value &&
    latest_meta.installer &&
    !INSTALLER_CONFIG.enbedded_metadata
  ) {
    if (
      !latest_meta.hashed.find(
        (e) => e.file_name === PROJECT_CONFIG.updaterName,
      )
    ) {
      const installerMeta: DfsMetadataHashInfo = {
        file_name: PROJECT_CONFIG.updaterName,
        size: latest_meta.installer.size,
        md5: latest_meta.installer.md5,
        xxh: latest_meta.installer.xxh,
        installer: true,
      };
      latest_meta.hashed.push(installerMeta);
    }
  }
  if (await installPrepare(latest_meta?.tag_name)) return runInstall();
  let hashKey = '';
  if (latest_meta.hashed.every((e) => e.md5)) {
    hashKey = 'md5';
  } else if (latest_meta.hashed.every((e) => e.xxh)) {
    hashKey = 'xxh';
  } else {
    throw new Error('更新服务端配置有误，不支持的哈希算法');
  }
  subStep.value = 1;
  percent.value = 5;
  const local_meta = (
    await ipcCheckLocalFiles(
      {
        source: source.value,
        hash_algorithm: hashKey,
        file_list: latest_meta.hashed.map((e) => e.file_name),
      },
      ({ payload }) => {
        const [currentValue, total] = payload;
        current.value = `${currentValue} / ${total}`;
        percent.value = 5 + (currentValue / total) * 15;
      },
      needElevate.value,
    )
  ).map((e) => {
    return {
      ...e,
      file_name: e.file_name.replace(source.value, ''),
    };
  });
  current.value = '校验本地文件……';
  const diff_files: Array<DfsUpdateTask> = [];
  const strip_first_slash = (s: string) => {
    let ss = s.replace(/\\/g, '/');
    if (ss.startsWith('/')) return ss.slice(1);
    return ss;
  };
  const userDataPath = PROJECT_CONFIG.userDataPath.map(replacePathEnvirables);
  const ignoreFolderPath = PROJECT_CONFIG.ignoreFolderPath || [];

  // 预先检查所有 ignoreFolderPath 是否非空（仅在更新场景下检查）
  const ignoreMap: string[] = [];
  if (isUpdate.value && ignoreFolderPath.length > 0) {
    for (const folder of ignoreFolderPath) {
      try {
        const fullPath = replacePathEnvirables(folder).replace(
          /[\\\/]+/g,
          sep(),
        );
        const [isEmpty] = await ipcIsFolderEmpty(fullPath);
        if (!isEmpty) {
          ignoreMap.push(fullPath.toLowerCase().replace(/[\\\/]+/g, sep()));
        }
      } catch (e) {
        // 预检查失败不阻塞安装，仅记录警告并跳过该规则
        warn(`ignoreFolderPath 检查失败 (${folder}), 将跳过该规则:`, e);
      }
    }
  }
  for (const item of latest_meta.hashed) {
    const local = local_meta.find(
      (e: { file_name: string }) =>
        strip_first_slash(e.file_name.toLowerCase()) ===
        strip_first_slash(item.file_name.toLowerCase()),
    );
    if (
      local &&
      userDataPath.some((userData) =>
        strip_first_slash(local.file_name)
          .toLowerCase()
          .startsWith(strip_first_slash(userData).toLowerCase()),
      )
    ) {
      continue;
    }

    // 新增的 ignoreFolderPath 检查
    // 关键：必须是更新场景 + 文件夹非空才跳过
    if (isUpdate.value && ignoreFolderPath.length > 0) {
      const itemCheckFullPath = `${source.value}${sep()}${item.file_name}`
        .toLowerCase()
        .replace(/[\\\/]+/g, sep());
      if (
        ignoreMap.some((ignoreFolder) => {
          return itemCheckFullPath.startsWith(ignoreFolder);
        })
      ) {
        continue;
      }
    }
    if (!local || local.hash !== item[hashKey as DfsMetadataHashType]) {
      let patch = latest_meta.patches?.find(
        (e) =>
          e.from[hashKey as DfsMetadataHashType] === local?.hash &&
          e.to[hashKey as DfsMetadataHashType] ===
            item[hashKey as DfsMetadataHashType],
      );
      let lpatch = latest_meta.patches?.find((e) =>
        INSTALLER_CONFIG.embedded_files?.some(
          (em) => em.name === e.from[hashKey as DfsMetadataHashType],
        ),
      );
      diff_files.push({
        ...item,
        patch,
        lpatch,
        downloaded: 0,
        running: false,
        old_hash: local?.hash,
        unwritable: local?.unwritable || false,
      });
    }
  }
  if (diff_files.length === 0) {
    await finishInstall(latest_meta);
    percent.value = 100;
    step.value = 4;
    return;
  }
  if (
    diff_files.find(
      (e) => e.unwritable && e.file_name !== PROJECT_CONFIG.updaterName,
    )
  ) {
    if (
      !INSTALLER_CONFIG.args.non_interactive &&
      !INSTALLER_CONFIG.args.silent &&
      !(await confirm(
        '检测到部分文件被占用，继续安装可能无法成功，是否继续？\n\n被占用的文件列表：' +
          diff_files
            .filter(
              (e) => e.unwritable && e.file_name !== PROJECT_CONFIG.updaterName,
            )
            .map((e) => e.file_name)
            .join('\n'),
      ))
    ) {
      step.value = 1;
      return;
    }
  }
  console.log('Files to install:', diff_files);

  // 使用 DFS2 来源时创建 DFS2 会话
  if (selectedSource.value.startsWith('dfs2+')) {
    current.value = '创建下载会话……';
    try {
      const ranges = collectDfs2Ranges(
        diff_files,
        INSTALLER_CONFIG.embedded_files || [],
        selectedSource.value,
        hashKey as DfsMetadataHashType,
      );

      if (ranges.length > 0) {
        const apiUrl = selectedSource.value.replace(/^dfs2\+packed\+/, '');

        // 从缓存获取资源版本
        const cache = dfsIndexCache.get(selectedSource.value);
        const resourceVersion = cache?.resource_version;

        const sessionId = await createDfs2Session(
          apiUrl,
          ranges,
          resourceVersion, // 使用元数据中的指定版本
          INSTALLER_CONFIG.args.dfs_extras || undefined,
        );

        log('DFS2 session created successfully:', sessionId);
      }
    } catch (e) {
      error('Failed to create DFS2 session:', e);
      await dialog_error(`创建下载会话失败: ${e}`);
      step.value = 1;
      return;
    }
  }

  // 插件会话创建
  const plugin = pluginManager.findPlugin(selectedSource.value);
  if (plugin?.createSession) {
    try {
      const ranges = collectDfs2Ranges(
        diff_files,
        INSTALLER_CONFIG.embedded_files || [],
        selectedSource.value,
        hashKey as DfsMetadataHashType,
      );

      if (ranges.length > 0) {
        const cleanUrl = pluginManager.getCleanUrl(selectedSource.value);
        if (!cleanUrl)
          throw new Error('Invalid plugin URL: ' + selectedSource.value);
        const sessionId = await plugin.createSession(cleanUrl, ranges);
        log('Plugin session created:', sessionId);
      }
    } catch (e) {
      error('Failed to create plugin session:', e);
      await dialog_error(`创建下载会话失败: ${e}`);
      step.value = 1;
      return;
    }
  }

  subStep.value = 2;
  current.value = '准备下载……';

  // 预处理文件，进行合并分组
  const { processedFiles } = preprocessFiles(
    diff_files,
    selectedSource.value,
    hashKey as DfsMetadataHashType,
    INSTALLER_CONFIG.embedded_files || [],
  );

  let stat: InstallStat = {
    speedLastSize: 0,
    lastTime: performance.now(),
    speed: 0,
  };
  progressInterval.value = setInterval(() => {
    // 更新虚拟文件的状态
    processedFiles.forEach((item) => {
      if ((item as VirtualMergedFile)._isMergedGroup) {
        const virtualFile = item as VirtualMergedFile;
        // 计算虚拟文件的总下载量（所有内部文件的下载量之和）
        virtualFile.downloaded = virtualFile._mergedInfo.files.reduce(
          (sum, f) => sum + f.downloaded,
          0,
        );
        // 更新虚拟文件的运行状态（任意内部文件运行中则虚拟文件运行中）
        virtualFile.running = virtualFile._mergedInfo.files.some(
          (f) => f.running,
        );
      }
      // 单文件无需处理，因为runDfsDownload直接更新了对象
    });

    // 计算总大小和已下载大小，直接使用processedFiles
    const total_size = processedFiles.reduce((acc, cur) => {
      if ((cur as VirtualMergedFile)._isMergedGroup) {
        const virtualFile = cur as VirtualMergedFile;
        // 使用实际文件大小总和，不是合并下载大小
        return (
          acc +
          virtualFile._mergedInfo.files.reduce(
            (sum, f) =>
              sum +
              ((!f.failed && (f?.patch?.size || f?.lpatch?.size)) || f.size),
            0,
          )
        );
      } else {
        const file = cur as DfsUpdateTask;
        return (
          acc +
          ((!file.failed && (file?.patch?.size || file?.lpatch?.size)) ||
            file.size)
        );
      }
    }, 0);

    const now = performance.now();
    const time_diff = now - stat.lastTime;
    const downloadedTotalSize = processedFiles.reduce((acc, cur) => {
      if ((cur as VirtualMergedFile)._isMergedGroup) {
        const virtualFile = cur as VirtualMergedFile;
        return (
          acc +
          virtualFile._mergedInfo.files.reduce(
            (sum, f) => sum + f.downloaded,
            0,
          )
        );
      } else {
        return acc + (cur as DfsUpdateTask).downloaded;
      }
    }, 0);
    if (time_diff > 100) {
      stat.speed = (downloadedTotalSize - stat.speedLastSize) / time_diff;
      stat.speedLastSize = downloadedTotalSize;
      stat.lastTime = now;
    }
    const speed = formatSize(stat.speed * 1000);
    const downloaded = formatSize(downloadedTotalSize);
    const total = formatSize(total_size);

    // 更新运行中任务显示逻辑
    const runningTasks: string[] = [];

    processedFiles
      .filter((e) => e.running)
      .forEach((e) => {
        if ((e as VirtualMergedFile)._isMergedGroup) {
          // 对于合并组，只显示未完成的文件进度
          const virtualFile = e as VirtualMergedFile;
          virtualFile._mergedInfo.files
            .filter((f) => f.downloaded < f.size) // 只显示未完成的文件
            .forEach((f) => {
              runningTasks.push(
                `${basename(f.file_name)} ${formatSize(f.downloaded)}/${formatSize(f.size)}`,
              );
            });
        } else {
          // 单文件正常显示
          runningTasks.push(
            `${basename(e.file_name)} ${formatSize(e.downloaded)}/${formatSize(e.size)}`,
          );
        }
      });

    current.value = `
      <span class="d-single-stat">${downloaded} / ${total} (${speed}/s)</span>
      <div class="d-single-list">
        <div class="d-single">
          ${runningTasks.join('</div><div class="d-single">')}
        </div>
      </div>
    `;
    percent.value = 20 + (downloadedTotalSize / total_size) * 80;
  }, 30);

  // 使用动态任务管理器进行下载
  const downloadContext: DownloadContext = {
    dfsSource: selectedSource.value,
    extras: INSTALLER_CONFIG.args.dfs_extras,
    local: INSTALLER_CONFIG.embedded_files || [],
    source: source.value,
    hashKey: hashKey as DfsMetadataHashType,
    elevate: needElevate.value,
  };

  const taskManager = new DownloadTaskManager(processedFiles);

  // 初始化任务
  processedFiles.forEach((item) => {
    let task;

    if ((item as VirtualMergedFile)._isMergedGroup) {
      task = new MergedGroupTask(
        item as VirtualMergedFile,
        downloadContext,
        taskManager,
      );
    } else {
      // 根据文件模式选择合适的任务类型
      const file = item as DfsUpdateTask;
      const mode = getFileInstallMode(
        file,
        INSTALLER_CONFIG.embedded_files || [],
        hashKey as DfsMetadataHashType,
      );

      if (mode === 'local') {
        task = new LocalFileTask(file, downloadContext);
      } else {
        // hybridpatch, 补丁, 直接 都使用 SingleFileTask
        task = new SingleFileTask(file, downloadContext, taskManager);
      }
    }

    taskManager.addTask(task);
  });

  await taskManager.waitForCompletion();

  const stats = taskManager.getStats();
  log('All tasks completed successfully:', stats);
  clearInterval(progressInterval.value);

  // 在清理前创建 networkInsights 快照，确保统计一致
  const serversSnapshot = [...networkInsights];

  // 下载完成后、后处理前立即清理 DFS2 会话
  await cleanupAllDfs2Sessions(serversSnapshot);

  // 清理插件会话
  if (plugin?.endSession) {
    try {
      const cleanUrl = pluginManager.getCleanUrl(selectedSource.value);
      if (cleanUrl) {
        await plugin.endSession(cleanUrl, { servers: serversSnapshot });
      }
    } catch (e) {
      warn('Plugin session cleanup failed:', e);
    }
  }

  if (
    latest_meta.deletes &&
    Array.isArray(latest_meta.deletes) &&
    latest_meta.deletes.length > 0
  ) {
    current.value = '删除旧版残留文件……';
    try {
      // 过滤掉 ignoreFolderPath 中的文件
      const filesToDelete = latest_meta.deletes.filter((deleteFile) => {
        // 如果是更新场景且有 ignoreMap（已检查过的非空文件夹）
        if (isUpdate.value && ignoreMap.length > 0) {
          // 构造待删除文件的完整路径
          const deleteFullPath = `${source.value}${sep()}${deleteFile}`
            .toLowerCase()
            .replace(/[\\\/]+/g, sep());

          // 检查文件是否在任何需要忽略的文件夹下
          const shouldIgnore = ignoreMap.some((ignoreFolder) => {
            return deleteFullPath.startsWith(ignoreFolder);
          });

          // 如果应该忽略，则不删除（返回 false）
          return !shouldIgnore;
        }
        // 默认情况下，保留在删除列表中
        return true;
      });

      await ipcRmList(
        filesToDelete.map((e) => `${source.value}${sep()}${e}`),
        needElevate.value,
      );
    } catch (e) {
      warn(e);
    }
  }

  await installRuntimes();

  current.value = '很快就好……';
  await finishInstall(latest_meta);
  current.value = '安装完成';
  step.value = 3;
  percent.value = 100;
}

async function runMirrorcInstall() {
  if (!mirrorcKey.value) {
    changeSelectedSource(selectedSource.value);
    return;
  }
  step.value = 2;
  let source_version = {
    product_version: '',
  } as { product_version: string };
  try {
    source_version = await invoke<{ product_version: string }>(
      'get_exe_version',
      {
        exeName: `${source.value}${sep()}${PROJECT_CONFIG.exeName}`,
      },
    );
  } catch (e) {}
  const source_url = new URL(selectedSource.value);
  if (!source_url.hostname) {
    await dialog_error(
      '无法获取Mirror酱数据，安装包可能已经损坏：' + selectedSource.value,
      '出错了',
    );
    error('Invalid Mirrorc source URL:', selectedSource.value);
    step.value = 1;
    return;
  }
  const mirrorc_status = await invoke<MirrorcUpdate>('get_mirrorc_status', {
    resourceId: source_url.hostname,
    cdk: mirrorcKey.value,
    currentVersion: source_version.product_version,
    channel: source_url.searchParams.get('channel') || 'stable',
    arch: source_url.searchParams.get('arch') || undefined,
    os: source_url.searchParams.get('os') || undefined,
  }).catch((e) => {
    return Promise.reject(`从获取Mirror酱获取更新数据失败: ${e}`);
  });
  const errorResult = processMirrorcError(mirrorc_status, 'install');
  if (errorResult) {
    await dialog_error(errorResult.message, '出错了');
    if (errorResult.showSourceDialog) {
      dialog.value = 'source';
    }
    step.value = 1;
    return;
  }
  if (mirrorc_status.data?.version_name === source_version.product_version) {
    await finishInstall();
    percent.value = 100;
    step.value = 4;
    return;
  }
  if (await installPrepare(`${mirrorc_status.data?.version_name || 'unknown'}`))
    return runMirrorcInstall();
  if (!mirrorc_status.data?.url) {
    await dialog_error(
      '从Mirror酱获取更新失败: 下载地址为空，请联系Mirror酱客服',
      '出错了',
    );
    return;
  }
  if (!mirrorc_status.data?.sha256) {
    await dialog_error(
      '从Mirror酱获取更新失败: 校验数据为空，请联系Mirror酱客服',
      '出错了',
    );
    return;
  }
  console.log(mirrorc_status);
  log('Mirrorc source version', source_version.product_version);
  log('Mirrorc target version', mirrorc_status.data.version_name);
  log('Mirrorc update mode', mirrorc_status.data.update_type);
  log('Mirrorc URL', mirrorc_status.data.url);
  const mirrorc_zip_url = mirrorc_status.data.url;
  const mirrorc_zip_path = `${source.value}${sep()}KachinaInstaller_Mirrorc_${mirrorc_status.data.sha256}.zip`;
  subStep.value = 1;
  percent.value = 5;
  current.value = '准备从Mirror酱下载……';
  let lastDownloaded = 0;
  let lastSpeedCalcTime = 0;
  let lastSpeedStr = '';
  await ipcRunMirrorcDownload(
    mirrorc_zip_url,
    mirrorc_zip_path,
    ({ payload }) => {
      if (payload.type === 'download') {
        const { downloaded, total } = payload;
        if (lastSpeedCalcTime !== 0) {
          const now = performance.now();
          const time_diff = now - lastSpeedCalcTime;
          if (time_diff > 100) {
            const speed = (downloaded - lastDownloaded) / time_diff;
            lastDownloaded = downloaded;
            lastSpeedCalcTime = now;
            lastSpeedStr = `(${formatSize(speed * 1000)}/s)`;
            lastSpeedCalcTime = now;
          }
        } else {
          lastSpeedCalcTime = performance.now();
        }
        current.value = `${formatSize(downloaded)} / ${formatSize(
          total,
        )} ${lastSpeedStr}`;
        percent.value = 5 + (downloaded / total) * 65;
      }
    },
    needElevate.value,
  );
  subStep.value = 2;
  current.value = '检查压缩包……';
  const [meta, changeset] = await ipcRunMirrorcInstall(
    mirrorc_zip_path,
    source.value,
    ({ payload }) => {
      console.log(payload);
      switch (payload.type) {
        case 'extract':
          current.value = `<div class="d-single-stat">解压 ${payload.file}</div>`;
          percent.value = 70 + (payload.count / payload.total) * 25;
          break;
        case 'delete':
          current.value = `<div class="d-single-stat">删除 ${payload.file}</div>`;
          percent.value = 97;
          break;
      }
    },
    needElevate.value,
  );
  console.log(changeset, meta);
  await installRuntimes();

  current.value = '很快就好……';
  await finishInstall(meta);
  current.value = '安装完成';
  step.value = 3;
  percent.value = 100;
}

async function getLnkPath() {
  const [program, desktop] = await invoke<InvokeGetDirsRes>('get_dirs', {
    elevated: needElevate.value,
  });
  const shortcutName = PROJECT_CONFIG.shortcutName || PROJECT_CONFIG.appName;
  return {
    programFolder: `${program}${sep()}${PROJECT_CONFIG.appName}`,
    program: `${program}${sep()}${PROJECT_CONFIG.appName}${sep()}${shortcutName}.lnk`,
    desktop: `${desktop}${sep()}${shortcutName}.lnk`,
    uninstall: `${program}${sep()}${PROJECT_CONFIG.appName}${sep()}卸载${shortcutName}.lnk`,
  };
}

/**
 * 安装期由宿主自建 / 改名的快捷方式（例如把 Kachina 建的
 * `GenshinFpsUnlocker.lnk` 规范成中文显示名 `原神帧率解锁.lnk`），不在 Kachina
 * 默认的清理范围里，卸载后会在桌面留下指向已删除 exe 的死图标。
 *
 * 配置项 `extraUninstallLnkNames` 只给**文件名**；这里用 Shell API 把四个真实
 * 目录都解析出来再拼完整路径：
 * - 公共桌面 / 用户桌面（宿主按可写性二选一，且桌面可能被 OneDrive 重定向，
 *   所以不能用 `%USERPROFILE%\Desktop` 之类的拼法）
 * - 公共开始菜单 / 用户开始菜单下的产品文件夹
 *
 * 这些路径交给 Rust 侧「尽力删除」，删不掉只记日志，不会让卸载失败。
 */
async function getExtraUninstallShortcutPaths(): Promise<string[]> {
  const names = PROJECT_CONFIG.extraUninstallLnkNames ?? [];
  if (names.length === 0) {
    return [];
  }
  const paths: string[] = [];
  for (const elevated of [true, false]) {
    try {
      const [programs, desk] = await invoke<InvokeGetDirsRes>('get_dirs', {
        elevated,
      });
      const productDir = `${programs}${sep()}${PROJECT_CONFIG.appName}`;
      paths.push(productDir);
      for (const name of names) {
        paths.push(`${desk}${sep()}${name}`);
        paths.push(`${productDir}${sep()}${name}`);
      }
    } catch (e) {
      warn(e);
    }
  }
  return paths;
}

async function finishInstall(
  latest_meta?: InvokeGetDfsMetadataRes,
): Promise<void> {
  const { program, desktop, uninstall } = await getLnkPath();
  const exePath = `${source.value}${sep()}${PROJECT_CONFIG.exeName}`;
  if (createLnk.value && !isUpdate.value) {
    await ipcCreateLnk(exePath, desktop, needElevate.value).catch(warn);
  }
  if (!isUpdate.value) {
    await ipcCreateLnk(exePath, program, needElevate.value).catch(warn);
  }
  if (
    !isUpdate.value ||
    INSTALLER_CONFIG.install_path_source.startsWith('REG')
  ) {
    try {
      await ipcCreateUninstaller(
        source.value,
        PROJECT_CONFIG.uninstallName,
        PROJECT_CONFIG.updaterName,
        needElevate.value,
      );
    } catch (e) {
      dialog_error(`创建卸载程序失败: ${e}`, '出错了');
      warn(e);
    }
    if (latest_meta) {
      try {
        await ipcWriteRegistry(
          {
            reg_name: PROJECT_CONFIG.regName,
            name: PROJECT_CONFIG.appName,
            version: latest_meta.tag_name || '0.0',
            exe: `${source.value}${sep()}${PROJECT_CONFIG.exeName}`,
            source: source.value,
            uninstaller: `${source.value}${sep()}${PROJECT_CONFIG.uninstallName}`,
            metadata: JSON.stringify(latest_meta),
            size: latest_meta.hashed.reduce((acc, cur) => acc + cur.size, 0),
            publisher: PROJECT_CONFIG.publisher,
          },
          needElevate.value,
        );
      } catch (e) {
        dialog_error(`写入注册表失败: ${e}`, '出错了');
        warn(e);
      }
    }
    await ipcCreateLnk(
      `${source.value}${sep()}${PROJECT_CONFIG.uninstallName}`,
      uninstall,
      needElevate.value,
    ).catch(log);
  }
  if (INSTALLER_CONFIG.args.silent) {
    const win = getCurrentWindow();
    win.close();
  }
}

async function install(): Promise<void> {
  try {
    void cleanupAllDfs2Sessions();
  } catch (e) {}
  try {
    if (installMode.value === 'mirrorc') {
      await runMirrorcInstall();
    } else {
      await runInstall();
    }
  } catch (e) {
    error(e);
    const errstr =
      e instanceof Error
        ? e.message || e.toString() // 使用 message 而不是 stack，更用户友好
        : typeof e === 'string'
          ? e
          : JSON.stringify(e);
    const logErrStr =
      e instanceof Error
        ? e.stack || e.toString() // 日志中保留完整的 stack
        : errstr;
    // 上游把 logErrStr 连同环境信息一起上报（sendInsight）；本项目已移除遥测，
    // 完整 stack 只写本地日志，弹窗仍然只给用户看更友好的 message。
    log('安装失败:', logErrStr);
    await dialog_error(errstr);

    // 发生错误时清理 DFS2 会话（仅 DFS 模式）
    if (installMode.value === 'default') {
      // 在清理前创建 networkInsights 快照，确保统计一致
      const serversSnapshot = [...networkInsights];

      await cleanupAllDfs2Sessions(serversSnapshot);

      // 清理插件会话
      const plugin = pluginManager.findPlugin(selectedSource.value);
      if (plugin?.endSession) {
        try {
          const cleanUrl = pluginManager.getCleanUrl(selectedSource.value);
          if (cleanUrl) {
            await plugin.endSession(cleanUrl, { servers: serversSnapshot });
          }
        } catch (e) {
          warn('Plugin session cleanup failed:', e);
        }
      }
    }

    step.value = 1;
    subStep.value = 0;
    percent.value = 0;
    current.value = '';
    clearInterval(progressInterval.value);
    progressInterval.value = 0;
  }
}

function processEmbeddedImage(base64Data: string | null) {
  if (!base64Data) {
    // 没有内嵌图片，使用默认值
    imageSource.value = new URL('./left.webp', import.meta.url).href;
    return;
  }

  try {
    // 解码 base64 以检查前 16 个字节
    const binaryString = atob(base64Data);
    const bytes = new Uint8Array(binaryString.length);
    for (let i = 0; i < binaryString.length; i++) {
      bytes[i] = binaryString.charCodeAt(i);
    }

    // 检查前 16 个字节是否全部为可打印 ASCII（0x20-0x7E）
    const first16Bytes = bytes.slice(0, Math.min(16, bytes.length));
    const isAscii = first16Bytes.every((byte) => byte >= 0x20 && byte <= 0x7e);

    if (isAscii) {
      // 这是 CSS，解码并注入
      const cssContent = new TextDecoder().decode(bytes);
      dynamicCss.value = cssContent;
      useDynamicCss.value = true;
      log('Loaded embedded CSS stylesheet');
    } else {
      // 这是图片，作为数据 URI 使用
      imageSource.value = `data:image/webp;base64,${base64Data}`;
      useDynamicCss.value = false;
      log('Loaded embedded image');
    }
  } catch (e) {
    error('Failed to process embedded image:', e);
    // 回退到默认值
    imageSource.value = new URL('./left.webp', import.meta.url).href;
  }
}

onMounted(async () => {
  try {
    const win = getCurrentWindow();
    const ps = [];
    ps.push(win.setTitle(' '));
    if (process.env.NODE_ENV === 'development') {
      ps.push(win.show());
    }
    let rsrc = await getSource(false);
    Object.assign(INSTALLER_CONFIG, rsrc);
    if (!rsrc.args.silent) {
      await win.show();
    }
    await Promise.all(ps);
    rsrc = await getSource(true);
    Object.assign(INSTALLER_CONFIG, rsrc);
    log('INSTALLER_CONFIG: ', {
      ...rsrc,
      embedded_config: {
        ...rsrc.embedded_config,
        source: Array.isArray(rsrc.embedded_config?.source)
          ? rsrc.embedded_config?.source.map((e) => ({ id: e.id, uri: e.uri }))
          : rsrc.embedded_config?.source,
      },
      embedded_index: undefined,
      embedded_files: undefined,
      embedded_image: undefined,
      enbedded_metadata: undefined,
    });
    if (INSTALLER_CONFIG.embedded_config) {
      Object.assign(PROJECT_CONFIG, INSTALLER_CONFIG.embedded_config);
      // 打包时内联了用户协议：要求用户主动勾选「我已阅读并同意」才放开安装按钮。
      // 没有内联协议时保持上游默认（视为已同意），不额外增加交互；
      // 静默 / 非交互安装走 onMounted 末尾的 安装()，不受这个勾选影响。
      if (hasAgreementContent(PROJECT_CONFIG.agreement)) {
        acceptEula.value = false;
      }
      // 处理内嵌图片/CSS
      processEmbeddedImage(INSTALLER_CONFIG.embedded_image);

      if (process.env.NODE_ENV === 'development') {
        if (
          INSTALLER_CONFIG.embedded_files &&
          INSTALLER_CONFIG.embedded_files.length > 0 &&
          !INSTALLER_CONFIG.embedded_files.find((e) => e.name === '\0CONFIG')
        ) {
          dialog_error('打包错误，请确保配置文件被正确打包');
        }
      }
    } else if (process.env.NODE_ENV === 'development') {
      dialog_error('未找到配置文件，请将配置文件放在exe同目录下');
    } else {
      await dialog_error('安装包损坏，请重新下载');
      const win = getCurrentWindow();
      win.close();
      return;
    }
    const xsrc = rsrc.embedded_config?.source;
    if (!xsrc) {
      throw new Error('打包错误，请确保配置文件被正确打包');
    }
    if (!Array.isArray(xsrc)) {
      selectedSource.value = xsrc;
    } else if (xsrc.length > 0) {
      selectedSource.value =
        xsrc.find((e) => e.id === rsrc.args.source)?.uri || xsrc[0]?.uri;
    }
    source.value =
      INSTALLER_CONFIG.args.target || INSTALLER_CONFIG.install_path;
    const seldir = await invoke<InvokeSelectDirRes>('select_dir', {
      exeName: PROJECT_CONFIG.exeName,
      silent: true,
      path: source.value,
    });
    if (seldir) {
      setUacByState(seldir.state, PROJECT_CONFIG.uacStrategy);
    }
    if (INSTALLER_CONFIG.embedded_index && INSTALLER_CONFIG.embedded_files) {
      let hasWrongIndex = false;
      for (const i of INSTALLER_CONFIG.embedded_index) {
        const target = INSTALLER_CONFIG.embedded_files.find(
          (e) => e.name === i.name,
        );
        if (!target) {
          log('Unfound index', target, i);
          hasWrongIndex = true;
          continue;
        }
        if (target.offset !== i.offset || target.raw_offset !== i.raw_offset) {
          log('Wrong index: pack=', target, 'index=', i);
          hasWrongIndex = true;
        }
      }
      if (hasWrongIndex) {
        if (process.env.NODE_ENV === 'development') {
          dialog_error('打包错误，请确保索引文件正确');
        } else {
          await dialog_error('安装包损坏，请重新下载');
          const win = getCurrentWindow();
          win.close();
          return;
        }
      }
    }
    if (INSTALLER_CONFIG.install_path_exists) isUpdate.value = true;
    await win.setTitle(PROJECT_CONFIG.windowTitle);
    INSTALLER_CONFIG.is_uninstall =
      INSTALLER_CONFIG.is_uninstall || INSTALLER_CONFIG.args.uninstall;
    if (INSTALLER_CONFIG.is_uninstall) {
      const uninstallConfig = await invoke(
        'read_uninstall_metadata',
        PROJECT_CONFIG,
      ).catch(log);
      log('UNINSTALL_METADATA: ', uninstallConfig);
      if (!uninstallConfig) {
        await dialog_error('未找到卸载配置文件，请重新安装后再卸载');
        if (process.env.NODE_ENV !== 'development') {
          const win = getCurrentWindow();
          win.close();
        }
        return;
      }
    }
    init.value = 1;
    // 应用无边框窗口设置
    if (PROJECT_CONFIG.windowBorderless === true) {
      try {
        await getCurrentWindow().setDecorations(false);
      } catch (e) {
        warn('Failed to set window borderless:', e);
      }
    } else {
      try {
        await getCurrentWindow().setDecorations(true);
      } catch (e) {
        warn('Failed to set window decorations:', e);
      }
    }
    init.value = 2;
    if (INSTALLER_CONFIG.args.silent || INSTALLER_CONFIG.args.non_interactive) {
      if (INSTALLER_CONFIG.args.uninstall || INSTALLER_CONFIG.is_uninstall) {
        uninstall();
      } else {
        install();
      }
    }
  } catch (e) {
    error(e);
    if (e instanceof Error)
      await dialog_error(e.stack || e.toString(), '安装程序初始化失败');
    else
      await dialog_error(
        typeof e === 'string' ? e : JSON.stringify(e),
        '安装程序初始化失败',
      );
    if (process.env.NODE_ENV !== 'development') {
      const win = getCurrentWindow();
      win.close();
    }
  }
});

// 组件卸载时清理
onUnmounted(() => {
  resetHiddenSourcesState();
});

function formatSize(size: number): string {
  if (size < 1024) {
    return `${size.toFixed(2)} B`;
  }
  if (size < 1024 * 1024) {
    return `${(size / 1024).toFixed(2)} KB`;
  }
  return `${(size / 1024 / 1024).toFixed(2)} MB`;
}

function basename(path: string): string {
  return path.replace(/\\/g, '/').split('/').pop() as string;
}

async function launch() {
  const mainExe = PROJECT_CONFIG.exeName;
  const fullPath = `${source.value}${sep()}${mainExe}`;
  await invoke('launch_and_exit', { path: fullPath });
}
async function exit() {
  const win = getCurrentWindow();
  win.close();
}

async function changeSource() {
  try {
    const seldir = await invoke<InvokeSelectDirRes>('select_dir', {
      path: source.value,
      exeName: PROJECT_CONFIG.exeName,
      silent: false,
    });
    if (seldir === null) return;
    log('SELECT_DIR: ', seldir);
    setUacByState(seldir.state, PROJECT_CONFIG.uacStrategy);
    isUpdate.value = seldir.upgrade;
    if (!seldir.empty && !seldir.upgrade) {
      const isDriveRoot = seldir.path.replace(/\\/g, '/').match(/^\w:\/$/);
      const confirmRes =
        isDriveRoot ||
        (await confirm(
          '您选择的目录不为空，是否创建新文件夹再安装？选【否】将可能影响原有数据。',
          '提示',
        ));
      if (confirmRes) {
        source.value =
          `${seldir.path}${sep()}${PROJECT_CONFIG.appName}`.replace(
            /\\\\/g,
            '\\',
          );
      } else {
        source.value = seldir.path;
      }
    } else {
      source.value = seldir.path;
    }
  } catch (e) {
    if (e instanceof Error) await dialog_error(e.stack || e.toString());
    else await dialog_error(JSON.stringify(e));
    throw e;
  }
}

const mirrorcTempUrl = ref('');
const mirrorcTempKey = ref('');
const mirrorcChecking = ref(false);
async function changeSelectedSource(url: string) {
  const isMirrorc = url.startsWith('mirrorc://');
  dialog.value = isMirrorc ? 'mirrorc' : '';
  if (isMirrorc) {
    try {
      mirrorcKey.value = await invoke('wincred_read', {
        target: `KachinaInstaller_MirrorChyanCDK_${PROJECT_CONFIG.appName}`,
      });
    } catch (e) {
      console.warn(e);
    }
    mirrorcTempUrl.value = url;
    mirrorcTempKey.value = mirrorcKey.value;
  } else {
    selectedSource.value = url;
    mirrorcTempUrl.value = '';
    mirrorcTempKey.value = '';
  }
}

async function changeMirrorcKey() {
  if (!mirrorcTempKey.value) {
    try {
      await invoke('wincred_delete', {
        target: `KachinaInstaller_MirrorChyanCDK_${PROJECT_CONFIG.appName}`,
      });
      mirrorcKey.value = '';
    } catch (e) {
      console.warn(e);
    }
  } else {
    if (mirrorcChecking.value) return;
    mirrorcChecking.value = true;
    const source_url = new URL(mirrorcTempUrl.value);
    if (!source_url.hostname) {
      await dialog_error(
        '无法获取Mirror酱数据，安装包可能已经损坏：' + selectedSource.value,
        '出错了',
      );
      mirrorcChecking.value = false;
      return;
    }
    const mirrorc_status = await invoke<MirrorcUpdate>('get_mirrorc_status', {
      resourceId: source_url.hostname,
      cdk: mirrorcTempKey.value,
      currentVersion: '',
      channel: source_url.searchParams.get('channel') || 'stable',
      arch: source_url.searchParams.get('arch') || undefined,
      os: source_url.searchParams.get('os') || undefined,
    });
    const errorResult = processMirrorcError(mirrorc_status, 'cdk-validation');
    if (errorResult) {
      await dialog_error(errorResult.message, '出错了');
      mirrorcChecking.value = false;
      return;
    }
    mirrorcKey.value = mirrorcTempKey.value;
    try {
      await invoke('wincred_write', {
        target: `KachinaInstaller_MirrorChyanCDK_${PROJECT_CONFIG.appName}`,
        token: mirrorcTempKey.value,
        comment: 'MirrorChyan CDK for BetterGI',
      });
    } catch (e) {
      console.warn(e);
    }
  }
  selectedSource.value = mirrorcTempUrl.value;
  dialog.value = '';
  mirrorcChecking.value = false;
}

async function dialog_error(message: string, title = '出错了'): Promise<void> {
  await invoke('error_dialog', {
    message: message.replace(new RegExp(location.origin, 'g'), ''),
    title,
  });
  if (INSTALLER_CONFIG.args.silent) {
    const win = getCurrentWindow();
    win.close();
  }
}
async function confirm(message: string, title = '提示'): Promise<boolean> {
  return await invoke<boolean>('confirm_dialog', { message, title });
}
/// 卸载前结束正在运行的主程序。
///
/// 进程活着的时候，它自己的 exe、`logs\` 与 WebView2 的界面缓存
/// （`%LOCALAPPDATA%\GenshinFpsUnlocker\EBWebView`）都被占用，删除会失败 ——
/// 上游只在安装流程（`installPrepare`）里做了「检测 → 询问 → 结束进程」，
/// 卸载流程没有，于是从「设置 → 应用」或开始菜单发起卸载时（主程序常驻托盘）
/// 必然留下残留。
async function killRunningAppForUninstall(): Promise<boolean> {
  const runningExes =
    (await ipcFindProcessByName(PROJECT_CONFIG.exeName).catch(log)) || [];
  if (runningExes.length === 0) return true;
  const ok =
    INSTALLER_CONFIG.args.non_interactive ||
    INSTALLER_CONFIG.args.silent ||
    (await confirm(
      `检测到${PROJECT_CONFIG.appName}正在运行。不结束进程的话，程序文件与用户数据（配置、日志、界面缓存）会因为被占用而删不掉，卸载后会留下残留。是否结束进程并继续卸载？`,
      '提示',
    ));
  if (!ok) return false;
  try {
    try {
      await Promise.all(
        runningExes.map((e) => ipcKillProcess(e[0], needElevate.value)),
      );
    } catch (e) {
      await Promise.all(runningExes.map((e) => ipcKillProcess(e[0], true)));
    }
  } catch (e) {
    // 结束不掉不拦卸载：后面的删除都是尽力而为，删不掉的会记日志
    warn('结束进程失败:', e);
    return true;
  }
  // 等句柄释放：WebView2 的缓存文件在进程退出后仍会被短暂占用
  await new Promise((resolve) => setTimeout(resolve, 1000));
  return true;
}

async function uninstall() {
  step.value = 5;
  if (!(await killRunningAppForUninstall())) {
    step.value = 1;
    return;
  }
  try {
    const uninstallConfig = (await invoke(
      'read_uninstall_metadata',
      PROJECT_CONFIG,
    )) as InvokeGetDfsMetadataRes;
    if (!uninstallConfig) {
      throw new Error('未找到卸载配置文件，请重新安装后再卸载');
    }
    await ipPrepare(needElevate.value);
    const { programFolder, desktop } = await getLnkPath();
    const extraShortcuts = await getExtraUninstallShortcutPaths();
    await ipcRunUninstall(
      {
        source: INSTALLER_CONFIG.install_path,
        files: [
          ...uninstallConfig.hashed.map((e) => e.file_name),
          PROJECT_CONFIG.updaterName,
        ],
        user_data_path: deleteUserData.value
          ? PROJECT_CONFIG.userDataPath.map(replacePathEnvirables)
          : [],
        extra_uninstall_path: [
          ...(PROJECT_CONFIG.extraUninstallPath?.map(replacePathEnvirables) ||
            []),
          programFolder,
          desktop,
        ],
        reg_name: PROJECT_CONFIG.regName,
        uninstall_name: PROJECT_CONFIG.uninstallName,
        // 安装期写入的注册表（开机自启动等）随卸载一并回收
        extra_uninstall_registry: PROJECT_CONFIG.extraUninstallRegistry ?? [],
        // 宿主自建/改名的快捷方式：尽力删除，失败不影响卸载
        extra_uninstall_shortcuts: extraShortcuts,
      },
      needElevate.value,
    );
    // Mirror酱 CDK 存在 Windows 凭据管理器里，卸载器不管这块（不是注册表也不是文件），
    // 自己清掉；没有这条凭据时Command会报错，忽略即可。
    await invoke('wincred_delete', {
      target: `KachinaInstaller_MirrorChyanCDK_${PROJECT_CONFIG.appName}`,
    }).catch((e) => warn('删除 Mirror酱 CDK 凭据失败:', e));
    step.value = 6;
    if (INSTALLER_CONFIG.args.silent) {
      const win = getCurrentWindow();
      win.close();
    }
  } catch (e) {
    error(e);
    const errstr =
      e instanceof Error
        ? e.stack || e.toString()
        : typeof e === 'string'
          ? e
          : JSON.stringify(e);
    await dialog_error(errstr);
    step.value = 1;
  }
}

function tplReplace(template: string, data: Record<string, string>): string {
  const regex = /\${(.*?)}/g;
  return template.replace(regex, (_match, key) => {
    return typeof data[key] !== 'undefined' ? data[key] : '';
  });
}
function replacePathEnvirables(path: string): string {
  return tplReplace(path, {
    INSTALL_PATH: INSTALLER_CONFIG.install_path,
    APP_NAME: PROJECT_CONFIG.appName,
  });
}
function setUacByState(
  state: 'Unwritable' | 'Writable' | 'Private',
  uacStrategy: ProjectConfig['uacStrategy'],
) {
  needElevate.value = false;
  switch (uacStrategy) {
    case 'force':
      needElevate.value = true;
      break;
    case 'prefer-admin':
      needElevate.value = state !== 'Private';
      break;
    case 'prefer-user':
      needElevate.value = state === 'Unwritable';
      break;
  }
}
function openMirrorc() {
  invoke('launch', {
    path: `https://mirrorchyan.com/?source=Kachina${PROJECT_CONFIG.appName}`,
  });
}

// 隐藏来源彩蛋功能
function handleKeyDown(event: KeyboardEvent) {
  // 仅在来源对话框打开时处理逗号键
  if (
    dialog.value !== 'source' ||
    (event.key !== ',' && event.code !== 'Comma')
  ) {
    return;
  }

  event.preventDefault();

  // 清除现有计时器
  if (commaTimeout.value) {
    clearTimeout(commaTimeout.value);
  }

  // 增加逗号计数
  commaCount.value++;

  // 检查是否已连续按下 5 次逗号
  if (commaCount.value >= 5) {
    showHiddenSources.value = true;
    commaCount.value = 0; // 重置计数器
    return;
  }

  // 设置计时器，在 2 秒后重置计数器
  commaTimeout.value = setTimeout(() => {
    commaCount.value = 0;
    commaTimeout.value = 0;
  }, 2000);
}

function resetHiddenSourcesState() {
  commaCount.value = 0;
  showHiddenSources.value = false;
  if (commaTimeout.value) {
    clearTimeout(commaTimeout.value);
    commaTimeout.value = 0;
  }
}
const minimize = async () => {
  const win = getCurrentWindow();
  win.minimize();
};
const close = async () => {
  const win = getCurrentWindow();
  win.close();
};
</script>
