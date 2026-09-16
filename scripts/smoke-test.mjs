/**
 * skillpay-service 冒烟测试。
 *
 * 覆盖 AI 按量付费（402）协议的本地可验证部分：
 *   1. 健康检查与服务信息
 *   2. 无凭据请求必须返回 402，并带 Payment-Needed 头
 *   3. 账单结构完整性（protocol / method 必需字段）
 *   4. seller_signature 的 RSA2 签名可被公钥验证
 *   5. 无效 Payment-Proof 必须退回 402，而不是 500
 *
 * 用法：
 *   node scripts/smoke-test.mjs [baseUrl]
 *   SKILLPAY_VERIFY_PUBLIC_KEY=<base64 应用公钥> node scripts/smoke-test.mjs
 *
 * 默认从 appsettings.Development.json 读取 Alipay:Aipay:AlipayPublicKey 作为验签公钥。
 * 本地联调时该字段放的就是本项目生成的开发公钥，因此签名验证是有意义的。
 */

import { createVerify } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const baseUrl = process.argv[2] ?? process.env.SKILLPAY_BASE ?? 'http://127.0.0.1:18080';
const skillCode = process.env.SKILLPAY_SKILL_CODE ?? 'demo-skill';
const paymentPath = `/v1/skills/${skillCode}/result`;

/**
 * 参与 seller_signature 的 8 个字段，以及各自所在的账单节点。
 * 注意 goods_name / seller_id / service_id 位于 method 节点，其余位于 protocol 节点，
 * 取值时不能只看 protocol。
 */
const SIGNED_FIELD_SOURCE = {
  amount: 'protocol',
  currency: 'protocol',
  goods_name: 'method',
  out_trade_no: 'protocol',
  pay_before: 'protocol',
  resource_id: 'protocol',
  seller_id: 'method',
  service_id: 'method'
};

const REQUIRED_PROTOCOL_FIELDS = [
  'out_trade_no',
  'amount',
  'currency',
  'resource_id',
  'pay_before',
  'seller_signature',
  'seller_sign_type',
  'seller_unique_id'
];

const REQUIRED_METHOD_FIELDS = [
  'seller_name',
  'seller_id',
  'seller_app_id',
  'goods_name',
  'seller_unique_id_key',
  'service_id'
];

const results = [];

function check(name, condition, detail = '') {
  results.push({ name, ok: Boolean(condition), detail });
  const mark = condition ? 'PASS' : 'FAIL';
  console.log(`[${mark}] ${name}${detail ? ` — ${detail}` : ''}`);
}

function fromBase64Url(value) {
  const normalized = value.replace(/-/g, '+').replace(/_/g, '/');
  const padded = normalized + '='.repeat((4 - (normalized.length % 4)) % 4);
  return Buffer.from(padded, 'base64').toString('utf8');
}

function toPem(publicKeyBase64) {
  const body = String(publicKeyBase64).replace(/-----[A-Z ]*-----/g, '').replace(/\s+/g, '');
  const lines = body.match(/.{1,64}/g) ?? [];
  return `-----BEGIN PUBLIC KEY-----\n${lines.join('\n')}\n-----END PUBLIC KEY-----\n`;
}

async function loadVerifyKey() {
  const fromEnv = process.env.SKILLPAY_VERIFY_PUBLIC_KEY;

  if (fromEnv && fromEnv.trim().length > 0) {
    return { key: fromEnv.trim(), source: '环境变量 SKILLPAY_VERIFY_PUBLIC_KEY' };
  }

  const here = dirname(fileURLToPath(import.meta.url));
  const settingsPath = join(here, '..', 'appsettings.Development.json');

  try {
    const raw = JSON.parse(await readFile(settingsPath, 'utf8'));
    const key = raw?.Alipay?.Aipay?.AlipayPublicKey;

    if (typeof key === 'string' && key.trim().length > 0) {
      return { key: key.trim(), source: 'appsettings.Development.json' };
    }
  } catch {
    // 读取失败时走下面的跳过分支。
  }

  return { key: null, source: '未找到' };
}

async function main() {
  console.log(`目标服务：${baseUrl}`);
  console.log(`付费端点：${paymentPath}`);
  console.log('');

  // --- 1. 健康检查 ---------------------------------------------------------
  const healthResponse = await fetch(`${baseUrl}/healthz`);
  const health = await healthResponse.json();
  check('健康检查返回 200', healthResponse.status === 200, `HTTP ${healthResponse.status}`);
  check('数据库可达', health.database === 'reachable', `database=${health.database}`);

  // --- 2. 服务信息 ---------------------------------------------------------
  const infoResponse = await fetch(`${baseUrl}/`);
  const info = await infoResponse.json();
  check('服务信息返回 200', infoResponse.status === 200, `HTTP ${infoResponse.status}`);
  check('协议标识正确', info.protocol === 'alipay-aipay-402', `protocol=${info.protocol}`);
  check('技能目录非空', Array.isArray(info.skills) && info.skills.length > 0, `${info.skills?.length ?? 0} 个技能`);

  // --- 3. 无凭据请求必须返回 402 -------------------------------------------
  const unpaid = await fetch(`${baseUrl}${paymentPath}`);
  const unpaidBody = await unpaid.json();
  check('无凭据返回 402', unpaid.status === 402, `HTTP ${unpaid.status}`);

  const paymentNeeded = unpaid.headers.get('payment-needed');
  check('响应带 Payment-Needed 头', Boolean(paymentNeeded), paymentNeeded ? `长度 ${paymentNeeded.length}` : '缺失');

  if (!paymentNeeded) {
    finish();
    return;
  }

  // --- 4. 账单结构完整性 ---------------------------------------------------
  let envelope;
  try {
    envelope = JSON.parse(fromBase64Url(paymentNeeded));
    check('Payment-Needed 可 Base64URL 解码为 JSON', true);
  } catch (error) {
    check('Payment-Needed 可 Base64URL 解码为 JSON', false, error.message);
    finish();
    return;
  }

  const protocol = envelope.protocol ?? {};
  const method = envelope.method ?? {};

  const missingProtocol = REQUIRED_PROTOCOL_FIELDS.filter((field) => !protocol[field]);
  const missingMethod = REQUIRED_METHOD_FIELDS.filter((field) => !method[field]);

  check('protocol 字段齐全', missingProtocol.length === 0, missingProtocol.join(', ') || '全部具备');
  check('method 字段齐全', missingMethod.length === 0, missingMethod.join(', ') || '全部具备');
  check('currency 为 CNY', protocol.currency === 'CNY', `currency=${protocol.currency}`);
  check('seller_sign_type 为 RSA2', protocol.seller_sign_type === 'RSA2', `seller_sign_type=${protocol.seller_sign_type}`);
  check('seller_unique_id_key 为 seller_id', method.seller_unique_id_key === 'seller_id', `key=${method.seller_unique_id_key}`);
  check(
    '响应体与账单 out_trade_no 一致',
    unpaidBody.out_trade_no === protocol.out_trade_no,
    `body=${unpaidBody.out_trade_no}`
  );

  // --- 5. RSA2 签名验证 ----------------------------------------------------
  const signedPairs = Object.keys(SIGNED_FIELD_SOURCE)
    .sort()
    .map((field) => [
      field,
      SIGNED_FIELD_SOURCE[field] === 'method' ? method[field] : protocol[field]
    ])
    .filter(([, value]) => value !== undefined && value !== null && String(value).length > 0);

  check(
    '签名原文涉及的全部 8 个字段都有值',
    signedPairs.length === Object.keys(SIGNED_FIELD_SOURCE).length,
    `${signedPairs.length} / ${Object.keys(SIGNED_FIELD_SOURCE).length}`
  );

  const signContent = signedPairs
    .map(([field, value]) => `${field}=${value}`)
    .join('&');

  const { key, source } = await loadVerifyKey();

  if (!key) {
    check('seller_signature RSA2 验签', false, '未提供验签公钥，已跳过');
  } else {
    const verifier = createVerify('RSA-SHA256');
    verifier.update(signContent, 'utf8');
    const valid = verifier.verify(toPem(key), protocol.seller_signature, 'base64');
    check('seller_signature RSA2 验签通过', valid, `公钥来源：${source}`);
  }

  // --- 6. pay_before 必须是未来时间 ----------------------------------------
  const payBefore = Date.parse(protocol.pay_before);
  check(
    'pay_before 是未来时间',
    Number.isFinite(payBefore) && payBefore > Date.now(),
    `pay_before=${protocol.pay_before}`
  );

  // --- 7. 无效 Payment-Proof 必须退回 402，而不是 500 ----------------------
  const bogusProof = Buffer.from(
    JSON.stringify({
      protocol: { payment_proof: 'invalid-proof-for-smoke-test', trade_no: '2026000000000000' },
      method: {}
    }),
    'utf8'
  )
    .toString('base64')
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '');

  const rejected = await fetch(`${baseUrl}${paymentPath}`, {
    headers: { 'Payment-Proof': bogusProof },
    signal: AbortSignal.timeout(60_000)
  });

  check(
    '无效凭据退回 402（不是 5xx）',
    rejected.status === 402,
    `HTTP ${rejected.status}`
  );

  finish();
}

function finish() {
  const failed = results.filter((result) => !result.ok);

  console.log('');
  console.log(`合计 ${results.length} 项，通过 ${results.length - failed.length} 项，失败 ${failed.length} 项。`);

  if (failed.length > 0) {
    console.log('未通过项：');
    for (const item of failed) {
      console.log(`  - ${item.name}${item.detail ? ` (${item.detail})` : ''}`);
    }

    process.exitCode = 1;
  }
}

main().catch((error) => {
  console.error('冒烟测试执行异常：', error);
  process.exitCode = 1;
});
