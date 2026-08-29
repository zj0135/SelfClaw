// Injected into every document in the WebView before page script runs. Only plugin panels use it; the
// application shell is the top frame and bails out immediately.
//
// A panel never talks to the host directly. It posts to its parent — the shell — which derives the
// panel's identity from the message's origin and the frame that sent it, then forwards the call over
// its own host bridge. The panel therefore cannot name a plugin other than itself, and cannot reach any
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
	var panelKey = new URLSearchParams(window.location.search).get('__selfclaw_panel') || '';

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

	window.addEventListener('message', function (event) {
		if (event.origin !== SHELL_ORIGIN || !event.data || event.data.__selfclaw !== 1) {
			return;
		}

		var message = event.data;
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

		if (message.kind === 'event') {
			if (message.type === 'handshake') {
				permissions = message.payload && message.payload.permissions ? message.payload.permissions : [];
				panelKey = (message.payload && message.payload.panelKey) || panelKey;
			}

			emit(message.type, message.payload);
		}
	});

	function requirePermission(permission) {
		if (permissions.length && permissions.indexOf(permission) < 0) {
			return Promise.reject(new Error('This panel does not declare the "' + permission + '" permission.'));
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

	window.selfclaw = {
		get panelKey() {
			return panelKey;
		},
		get permissions() {
			return permissions.slice();
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
