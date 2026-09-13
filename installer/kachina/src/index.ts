import { createApp } from 'vue';
import App from './App.vue';
import './index.css';

createApp(App).mount('#root');

if (process.env.NODE_ENV !== 'development') {
  window.addEventListener('contextmenu', (e) => {
    e.preventDefault();
  });
  document.addEventListener('keydown', function (event) {
    // 禁止 F5、Ctrl+R（Windows/Linux）和 Command+R（Mac）刷新页面
    if (
      event.key === 'F5' ||
      (event.ctrlKey && event.key === 'r') ||
      (event.metaKey && event.key === 'r')
    ) {
      event.preventDefault();
    }
  });
}
