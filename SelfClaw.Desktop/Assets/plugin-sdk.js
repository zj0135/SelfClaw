// Injected into every document in the WebView before page script runs. Only Plugin views use it; the
// application shell is the top frame and bails out immediately.
//
// A view never talks to the host directly. It posts to its parent — the shell — which derives the
// view's identity from the message's origin and the frame that sent it, then forwards the call over
// its own host bridge. The view therefore cannot name a Plugin other than itself, and cannot reach any
// host message type the shell does not deliberately expose here.
(function () {
	if (window.parent === window) {
		return;
	}

	var SHELL_ORIGIN = 'https://appassets.selfclaw.local';
	var pending = new Map();
	var handlers = new Map();
	var sequence = 0;
	var permissions = [];
	var handshakeReceived = false;
	var viewKey = new URLSearchParams(window.location.search).get('__selfclaw_view') || '';
	var slot = 'right';
	// The shell reports what the surrounding chrome looks like; it does not push its own colors in.
	// A view reads this to pick its own palette, and re-reads it on every 'appearance-changed'.
	var appearance = { theme: 'light', mode: 'system', uiFontFamily: '', uiFontScale: 1, codeFontFamily: '', codeFontScale: 1 };
	// Geometry facts, only ever the five documented landmarks; every key may be null.
	var anchors = { titlebar: null, sidebar: null, stage: null, composer: null, dock: null };

	function send(message) {
		window.parent.postMessage(Object.assign({ __selfclaw: 1 }, message), SHELL_ORIGIN);
	}

	function call(op, args) {
		var id = 'p' + ++sequence;
		return new Promise(function (resolve, reject) {
			pending.set(id, { resolve: resolve, reject: reject });
			send({ kind: 'request', id: id, op: op, args: args || {} });
		});
	}

	function emit(type, payload) {
		var set = handlers.get(type);
		if (!set) {
			return;
		}

		set.forEach(function (handler) {
			try {
				handler(payload);
			} catch (error) {
				console.error('[selfclaw] handler for "' + type + '" threw', error);
			}
		});
	}

	function handleMessage(message) {
		if (message.kind === 'response') {
			var entry = pending.get(message.id);
			if (!entry) {
				return;
			}

			pending.delete(message.id);
			if (message.ok) entry.resolve(message.result);
			else entry.reject(new Error(message.error || 'SelfClaw host call failed.'));
			return;
		}

		if (message.kind !== 'event') {
			return;
		}

		if (message.type === 'handshake') {
			var payload = message.payload || {};
			permissions = payload.permissions || [];
			viewKey = payload.viewKey || viewKey;
			slot = payload.slot || slot;
			handshakeReceived = true;
			if (payload.appearance) appearance = payload.appearance;
			if (payload.anchors) anchors = payload.anchors;
			if (slot === 'floating') startFloating();
		}

		// Cached before the handlers run, so selfclaw.appearance / selfclaw.layout.anchors are already
		// current inside them.
		if (message.type === 'appearance-changed' && message.payload) {
			appearance = message.payload;
		}

		if (message.type === 'anchors-changed' && message.payload) {
			anchors = message.payload;
		}

		// 外壳解除了武装（可能是它自己决定的，也可能是回应我们的 release）：把去重标记复位，
		// 否则下次进入矩形时我们会以为还在武装中，少发一次 release。
		if (message.type === 'hit-released') {
			regions.armed = false;
		}

		emit(message.type, message.payload);
	}

	window.addEventListener('message', function (event) {
		if (event.origin !== SHELL_ORIGIN || !event.data || event.data.__selfclaw !== 1) {
			return;
		}

		handleMessage(event.data);
	});

	function requirePermission(permission) {
		// The host is authoritative and always re-checks. Before the handshake the view has not been told
		// its permissions yet, so rejecting here would turn a normal call into a confusing local error.
		if (!handshakeReceived) {
			return null;
		}

		if (permissions.indexOf(permission) < 0) {
			return Promise.reject(new Error('This view does not declare the "' + permission + '" permission.'));
		}

		return null;
	}

	function workspaceOp(op, permission) {
		return function () {
			var denied = requirePermission(permission);
			if (denied) {
				return denied;
			}

			return call(op, arguments[0] || {});
		};
	}

	// ---------------------------------------------------------------------------------------------
	// 悬浮视图：交互矩形
	//
	// 悬浮层默认对宿主完全透明（pointer-events: none），只有这里声明的矩形在指针命中时才由外壳
	// 临时把本帧变成可交互。未声明矩形的悬浮视图是纯视觉的，不可能拦截任何输入。
	//
	// 自动模式取 [data-selfclaw-interactive] 元素的可见矩形并集；也可以用 setInteractive(rects)
	// 直接提交（canvas / 自绘命中测试的插件），setInteractive(null) 回到自动模式。
	// ---------------------------------------------------------------------------------------------
	var INTERACTIVE_SELECTOR = '[data-selfclaw-interactive]';
	var regions = { started: false, manual: false, rects: null, sent: '', scheduled: 0, armed: false, observed: new Set(), resizeObserver: null };

	function startFloating() {
		injectTransparentDefaults();
		if (regions.started) return;
		regions.started = true;

		if (typeof ResizeObserver === 'function') {
			regions.resizeObserver = new ResizeObserver(scheduleRegions);
		}

		if (typeof MutationObserver === 'function') {
			new MutationObserver(function () {
				observeInteractive();
				scheduleRegions();
			}).observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-selfclaw-interactive'] });
		}

		document.addEventListener('scroll', scheduleRegions, true);
		document.addEventListener('transitionend', scheduleRegions, true);
		window.addEventListener('resize', scheduleRegions);
		document.addEventListener('pointermove', onPointerMove);
		document.addEventListener('mouseleave', releasePointer);

		observeInteractive();
		scheduleRegions();
	}

	// A default, not a decision: inserted first so any background the page declares later still wins.
	// Without it a page that never styles its root would paint opaque white over the whole window.
	function injectTransparentDefaults() {
		if (document.getElementById('__selfclaw_floating_defaults')) return;
		// 注入发生在文档创建时，head 可能还没解析出来。
		var target = document.head || document.documentElement;
		if (!target) return;
		var style = document.createElement('style');
		style.id = '__selfclaw_floating_defaults';
		style.textContent = 'html, body { background: transparent; }';
		target.insertBefore(style, target.firstChild);
	}

	function observeInteractive() {
		if (!regions.resizeObserver) return;
		var present = new Set();
		var nodes = document.querySelectorAll(INTERACTIVE_SELECTOR);
		for (var i = 0; i < nodes.length; i++) {
			present.add(nodes[i]);
			if (!regions.observed.has(nodes[i])) {
				regions.observed.add(nodes[i]);
				regions.resizeObserver.observe(nodes[i]);
			}
		}

		regions.observed.forEach(function (element) {
			if (present.has(element) && element.isConnected) return;
			regions.observed.delete(element);
			regions.resizeObserver.unobserve(element);
		});
	}

	function collectRects() {
		if (regions.manual) return regions.rects || [];
		var results = [];
		var nodes = document.querySelectorAll(INTERACTIVE_SELECTOR);
		for (var i = 0; i < nodes.length; i++) {
			var rect = nodes[i].getBoundingClientRect();
			if (rect.width > 0 && rect.height > 0) {
				results.push({ x: rect.left, y: rect.top, width: rect.width, height: rect.height });
			}
		}

		return results;
	}

	function scheduleRegions() {
		if (!regions.started || regions.scheduled) return;
		regions.scheduled = window.requestAnimationFrame(function () {
			regions.scheduled = 0;
			publishRegions();
		});
	}

	function publishRegions() {
		var rects = collectRects();
		var serialized = JSON.stringify(rects);
		if (serialized === regions.sent) return;
		regions.sent = serialized;
		send({ kind: 'notice', type: 'hit-regions', payload: { rects: rects } });
	}

	function insideAnyRegion(x, y) {
		var rects = collectRects();
		for (var i = 0; i < rects.length; i++) {
			var rect = rects[i];
			if (x >= rect.x && x < rect.x + rect.width && y >= rect.y && y < rect.y + rect.height) return true;
		}

		return false;
	}

	// Only reached while the shell has armed this frame; the first move is the signal that it did.
	function onPointerMove(event) {
		regions.armed = true;
		if (!insideAnyRegion(event.clientX, event.clientY)) releasePointer();
	}

	function releasePointer() {
		if (!regions.armed) return;
		regions.armed = false;
		send({ kind: 'notice', type: 'hit-regions-release' });
	}

	function setInteractive(rects) {
		if (rects === null || rects === undefined) {
			regions.manual = false;
			regions.rects = null;
		} else {
			regions.manual = true;
			regions.rects = Array.prototype.slice.call(rects, 0, 64).map(function (rect) {
				return { x: Number(rect.x) || 0, y: Number(rect.y) || 0, width: Number(rect.width) || 0, height: Number(rect.height) || 0 };
			});
		}

		regions.sent = '';
		scheduleRegions();
	}

	window.selfclaw = {
		get viewKey() {
			return viewKey;
		},
		get slot() {
			return slot;
		},
		get permissions() {
			return permissions.slice();
		},
		get appearance() {
			return Object.assign({}, appearance);
		},
		ready: function () {
			send({ kind: 'ready' });
		},
		getContext: function () {
			return call('context.get');
		},
		insertPrompt: function (text) {
			var denied = requirePermission('host.composer.write');
			return denied || call('composer.insert', { text: String(text == null ? '' : text) });
		},
		// 关闭自己这个视图：外壳本地处理，不往返宿主，也不需要额外权限。
		close: function () {
			return call('view.close');
		},
		layout: {
			get anchors() {
				return Object.assign({}, anchors);
			},
			setInteractive: setInteractive,
		},
		workspace: {
			list: workspaceOp('workspace.list', 'host.workspace.read'),
			glob: workspaceOp('workspace.glob', 'host.workspace.read'),
			read: workspaceOp('workspace.read', 'host.workspace.read'),
			search: workspaceOp('workspace.search', 'host.workspace.read'),
		},
		on: function (type, handler) {
			var set = handlers.get(type);
			if (!set) {
				set = new Set();
				handlers.set(type, set);
			}

			set.add(handler);
			return function () {
				set.delete(handler);
			};
		},
	};

	send({ kind: 'hello' });
})();
