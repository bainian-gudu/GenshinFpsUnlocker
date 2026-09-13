/// <reference types="@rsbuild/core/types" />

declare module '*.vue' {
  import type { DefineComponent } from 'vue';

  // biome-ignore lint/complexity/noBannedTypes: 环境类型声明
  const component: DefineComponent<{}, {}, any>;
  export default component;
}

// process.env.NODE_ENV 由运行环境定义
declare const process: {
  env: {
    NODE_ENV: 'development' | 'production';
  };
};
