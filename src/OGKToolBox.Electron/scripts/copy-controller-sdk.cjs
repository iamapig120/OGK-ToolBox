'use strict';
const fs = require('node:fs');
const path = require('node:path');
const destination = path.resolve(__dirname, '../dist-electron/sdk/controller-provider');
fs.mkdirSync(destination, { recursive: true });
fs.copyFileSync(path.resolve(__dirname, '../../..', 'sdk/controller-provider/server.cjs'), path.join(destination, 'server.cjs'));
