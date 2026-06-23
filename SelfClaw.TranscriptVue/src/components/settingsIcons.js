// Centralized inline SVG markup for the settings view.
// Each entry is a full <svg> string (trusted, static) rendered through SettingsIcon.vue.
// Per-icon stroke widths / fills are preserved from the original HTML design.

const S = 'fill="none" stroke="currentColor" stroke-linecap="round" stroke-linejoin="round"';

export const ICONS = {
	// brand / navigation
	code: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><path d="M4 17l6-6-6-6"/><line x1="12" y1="19" x2="20" y2="19"/></svg>`,
	providers: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><rect x="3" y="4" width="18" height="6" rx="2"/><rect x="3" y="14" width="18" height="6" rx="2"/><line x1="7" y1="7" x2="7.01" y2="7"/><line x1="7" y1="17" x2="7.01" y2="17"/></svg>`,
	models: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><polygon points="12 2 2 7 12 12 22 7 12 2"/><polyline points="2 17 12 22 22 17"/><polyline points="2 12 12 17 22 12"/></svg>`,
	plugins: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><path d="M4 7h3a2 2 0 0 0 2-2 2 2 0 1 1 4 0 2 2 0 0 0 2 2h3v3a2 2 0 0 1-2 2 2 2 0 1 0 0 4 2 2 0 0 1 2 2v3h-3a2 2 0 0 1-2-2 2 2 0 1 0-4 0 2 2 0 0 1-2 2H4v-3a2 2 0 0 0 2-2 2 2 0 1 0 0-4 2 2 0 0 0-2-2z"/></svg>`,
	mcp: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><circle cx="6" cy="6" r="2.5"/><circle cx="6" cy="18" r="2.5"/><circle cx="18" cy="12" r="2.5"/><path d="M8.5 6H13a2.5 2.5 0 0 1 2.5 2.5v1M8.5 18H13a2.5 2.5 0 0 0 2.5-2.5v-1"/></svg>`,
	about: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><circle cx="12" cy="12" r="9"/><line x1="12" y1="11" x2="12" y2="16"/><line x1="12" y1="8" x2="12.01" y2="8"/></svg>`,

	// block titles
	sun: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><circle cx="12" cy="12" r="4"/><path d="M12 2v2M12 20v2M2 12h2M20 12h2M5 5l1.5 1.5M17.5 17.5L19 19M19 5l-1.5 1.5M6.5 17.5L5 19"/></svg>`,
	globe: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18"/></svg>`,
	folder: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>`,

	// generic actions
	plus: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><line x1="12" y1="5" x2="12" y2="19"/><line x1="5" y1="12" x2="19" y2="12"/></svg>`,
	search: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><circle cx="11" cy="11" r="7"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>`,
	zap: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><path d="M13 2L3 14h7l-1 8 10-12h-7z"/></svg>`,
	chevronRight: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><polyline points="9 6 15 12 9 18"/></svg>`,
	close: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><line x1="6" y1="6" x2="18" y2="18"/><line x1="18" y1="6" x2="6" y2="18"/></svg>`,
	eye: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><path d="M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7-10-7-10-7z"/><circle cx="12" cy="12" r="3"/></svg>`,
	eyeOff: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><path d="M17.9 17.9A10.4 10.4 0 0 1 12 19c-6.5 0-10-7-10-7a18 18 0 0 1 5.1-5.9M9.9 5.2A10 10 0 0 1 12 5c6.5 0 10 7 10 7a18 18 0 0 1-2.3 3.3M9.5 9.5a3 3 0 0 0 4 4"/><line x1="3" y1="3" x2="21" y2="21"/></svg>`,
	copy: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><rect x="9" y="9" width="11" height="11" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h10"/></svg>`,
	check: `<svg viewBox="0 0 24 24" ${S} stroke-width="2.4"><polyline points="20 6 9 17 4 12"/></svg>`,
	spin: `<svg viewBox="0 0 24 24" ${S} stroke-width="2.2" style="animation:settings-spin .8s linear infinite"><path d="M21 12a9 9 0 1 1-6.2-8.5"/></svg>`,
	play: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><polygon points="6 4 20 12 6 20 6 4"/></svg>`,
	stop: `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="6" y="6" width="12" height="12" rx="1.5"/></svg>`,
	trash: `<svg viewBox="0 0 24 24" ${S} stroke-width="2"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/></svg>`,
	star: `<svg viewBox="0 0 24 24" fill="currentColor"><path d="M12 2l2.9 6.3 6.9.6-5.2 4.6 1.6 6.8L12 17.3 5.8 20.9l1.6-6.8L2.2 8.9l6.9-.6z"/></svg>`,

	// plugin icons
	layers: `<svg viewBox="0 0 24 24" ${S} stroke-width="1.8"><path d="M3 7l9-4 9 4-9 4-9-4z"/><path d="M3 12l9 4 9-4M3 17l9 4 9-4"/></svg>`,
	refresh: `<svg viewBox="0 0 24 24" ${S} stroke-width="1.8"><path d="M21 12a9 9 0 1 1-3-6.7M21 4v5h-5"/></svg>`,
	chatDoc: `<svg viewBox="0 0 24 24" ${S} stroke-width="1.8"><path d="M4 4h16v12H5.2L4 17.5z"/><path d="M8 9h8M8 12h5"/></svg>`,

	// provider / mcp brand-ish icons
	anthropic: `<svg viewBox="0 0 24 24" ${S} stroke-width="1.8"><path d="M7 20l5-16 5 16M9.2 14h5.6"/></svg>`,
	openai: `<svg viewBox="0 0 24 24" ${S} stroke-width="1.7"><circle cx="12" cy="12" r="9"/><path d="M12 3a9 9 0 0 0 0 18M3 12h18"/></svg>`,
	deepseek: `<svg viewBox="0 0 24 24" ${S} stroke-width="1.8"><path d="M3 12a9 9 0 1 0 9-9c4 4 4 14 0 18"/><circle cx="9" cy="10" r="1"/></svg>`,
	box: `<svg viewBox="0 0 24 24" ${S} stroke-width="1.8"><rect x="4" y="4" width="16" height="16" rx="3"/><path d="M9 9h6v6H9z"/></svg>`,
	server: `<svg viewBox="0 0 24 24" ${S} stroke-width="1.8"><path d="M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/></svg>`,
	git: `<svg viewBox="0 0 24 24" ${S} stroke-width="1.8"><path d="M9 19c-4 1.5-4-2.5-6-3m12 5v-3.5c0-1 .1-1.4-.5-2 2.8-.3 5.5-1.4 5.5-6a4.6 4.6 0 0 0-1.3-3.2 4.3 4.3 0 0 0-.1-3.2s-1-.3-3.5 1.3a12 12 0 0 0-6.3 0C6.3 3.1 5.3 3.4 5.3 3.4a4.3 4.3 0 0 0-.1 3.2A4.6 4.6 0 0 0 4 9.8c0 4.6 2.7 5.7 5.5 6-.4.4-.5 1-.5 2V21"/></svg>`,
	database: `<svg viewBox="0 0 24 24" ${S} stroke-width="1.8"><ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v14c0 1.7 3.6 3 8 3s8-1.3 8-3V5M4 12c0 1.7 3.6 3 8 3s8-1.3 8-3"/></svg>`,
};
