// git 统一 diff 文本 → hunk / 行结构。纯解析，不碰 DOM、不依赖宿主。
//
//   · 第一个 @@ 之前的文件头（diff --git / index / --- +++ / mode / similarity / rename）整段跳过；
//   · `\ No newline at end of file` 记到上一行的 noNewline；
//   · 行尾 \r 去掉（CRLF 文件的 diff 会带着它）；
//   · 冲突文件是 combined diff（@@@ 头、两列标记）：任一列是 + / − 就降级成 add / remove。
const HUNK_HEADER = /^@@ -(\d+)(?:,\d+)? \+(\d+)(?:,\d+)? @@/;
const COMBINED_HEADER = /^@@@ -(\d+)(?:,\d+)? -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@@/;

export function parseUnifiedDiff(patch) {
	const hunks = [];
	if (typeof patch !== 'string' || patch.length === 0) return { hunks };
	const lines = patch.split('\n');
	if (lines[lines.length - 1] === '') lines.pop();
	const cursor = { old: 0, new: 0 };
	let hunk = null;
	let combined = false;
	for (const raw of lines) {
		const line = raw.endsWith('\r') ? raw.slice(0, -1) : raw;
		const header = HUNK_HEADER.exec(line) ?? COMBINED_HEADER.exec(line);
		if (header) {
			combined = line.startsWith('@@@');
			cursor.old = Number(header[1]);
			cursor.new = Number(header[2]);
			// context 是 @@ 之后 git 附带的函数 / 节标题，oldStart / newStart 用来判断前面是否有被省略的行。
			hunk = { header: line, context: line.slice(header[0].length).trim(), oldStart: cursor.old, newStart: cursor.new, lines: [] };
			hunks.push(hunk);
			continue;
		}
		if (!hunk) continue;
		if (line.startsWith('\\')) {
			const last = hunk.lines[hunk.lines.length - 1];
			if (last) last.noNewline = true;
			continue;
		}
		hunk.lines.push(combined ? readCombinedLine(line, cursor) : readLine(line, cursor));
	}
	return { hunks };
}

function readLine(line, cursor) {
	const marker = line[0] ?? ' ';
	return classify(marker === '+', marker === '-', line.slice(1), cursor);
}

function readCombinedLine(line, cursor) {
	const markers = line.slice(0, 2);
	return classify(markers.includes('+'), markers.includes('-'), line.slice(2), cursor);
}

function classify(isAdd, isRemove, text, cursor) {
	if (isAdd) return { kind: 'add', oldNumber: null, newNumber: cursor.new++, text };
	if (isRemove) return { kind: 'remove', oldNumber: cursor.old++, newNumber: null, text };
	return { kind: 'context', oldNumber: cursor.old++, newNumber: cursor.new++, text };
}
