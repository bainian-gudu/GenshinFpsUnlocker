<template>
  <div class="dialog">
    <div class="dialog-title">
      <slot name="title"></slot>
    </div>
    <div class="dialog-desc">
      <slot name="desc"></slot>
    </div>
    <div class="dialog-body">
      <slot name="body"></slot>
    </div>
    <div class="dialog-footer">
      <slot name="footer"></slot>
    </div>
  </div>
</template>

<script setup lang="ts"></script>

<style scoped>
.dialog {
  position: absolute;
  top: 4px;
  left: 16px;
  right: 16px;
  bottom: 0;
  /* 纵向 flex：正文吃掉剩余高度、footer 永远占自己那一份。
     安装窗口只有 520x250（见 src-tauri/src/main.rs 的 base_width/height），
     靠绝对定位摆按钮很容易压到正文上。 */
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.dialog-body {
  flex: 1 1 auto;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.dialog-footer {
  flex: 0 0 auto;
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  padding: 6px 8px 10px;
}
</style>
