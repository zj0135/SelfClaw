import { createApp } from 'vue';
import App from './App.vue';
import { transcriptBridge } from './composables/transcriptBridge.js';
import { useToast } from './composables/useToast.js';
import { initAppearance } from './composables/useAppearance.js';
// tokens 必须先于其它样式：markdown.css 与各组件的 var() 都依赖它。
import './styles/tokens.css';
import './styles/markdown.css';

// 必须在 mount 之前：这样字号、字体、颜色在第一帧内容上屏时就已经是最终值，
// 不会先按默认值排一遍版再跳。
initAppearance();

const app = createApp(App);
app.config.errorHandler = (error, instance) => {
    console.error(error);
    let component = instance?.$;
    let affectsTranscript = component?.type?.__name === 'App';
    while (component && !affectsTranscript) {
        affectsTranscript = ['TranscriptPanel', 'AppSidebar'].includes(component.type?.__name);
        component = component.parent;
    }
    if (affectsTranscript) transcriptBridge.reportRenderFailure(error);
    else useToast().showToast(error?.message || String(error));
};
app.mount('#app');
