const fs = require('fs')
const readline = require('readline')
const { spawn } = require('child_process')

if (process.argv.includes('--child')) {
  setInterval(() => {}, 60_000)
  return
}

const pidFile = process.argv[2]
const hangInitialize = process.argv.includes('--hang-initialize')
if (!pidFile) {
  process.exit(2)
}

const child = spawn(process.execPath, [__filename, '--child'], {
  detached: false,
  stdio: 'ignore'
})
child.unref()
fs.writeFileSync(pidFile, JSON.stringify({ parentPid: process.pid, childPid: child.pid }))

const input = readline.createInterface({ input: process.stdin })
input.on('line', line => {
  let request
  try {
    request = JSON.parse(line)
  } catch {
    return
  }

  if (request.id === undefined) {
    return
  }

  let result = {}
  if (request.method === 'initialize') {
    if (hangInitialize) {
      return
    }

    result = {
      protocolVersion: request.params?.protocolVersion ?? '2025-06-18',
      capabilities: { tools: {} },
      serverInfo: { name: 'selfclaw-process-tree-fixture', version: '1.0.0' }
    }
  } else if (request.method === 'tools/list') {
    result = {
      tools: [{ name: 'fixture_echo', description: 'Echo fixture', inputSchema: { type: 'object' } }]
    }
  } else if (request.method === 'tools/call') {
    result = {
      content: [{ type: 'text', text: `echo: ${request.params?.arguments?.value ?? ''}` }],
      isError: false
    }
  }

  process.stdout.write(`${JSON.stringify({ jsonrpc: '2.0', id: request.id, result })}\n`)
})

input.on('close', () => process.exit(0))
