# GPM Pay — AI Integration Skill

> Tài liệu này dành cho AI assistant. Hãy đọc và giúp người dùng tích hợp GPM Pay
> (cổng đối soát ngân hàng tự động) vào dự án của họ. Khi viết code, hãy hỏi người
> dùng các giá trị bí mật (API token, webhook secret) thay vì tự bịa.

## Tổng quan
GPM Pay theo dõi biến động số dư tài khoản ngân hàng và bắn webhook realtime khi có
giao dịch. Bạn tích hợp bằng REST API + webhook.

- **API base URL:** `https://api.gpmpay.com/api/v1`
- **Định dạng:** JSON, mọi response bọc trong `{ statusCode, message, data }`.
- **Phân trang:** `data.data[]` là mảng kết quả, `data.meta` là thông tin trang
  (`{ page, limit, totalItems, totalPages }`). `limit` bị giới hạn tối đa 50.

> **Dự án Node.js/TypeScript: hãy dùng SDK chính thức thay vì gọi fetch thô.**
> `pnpm add @gpmpay/sdk` — sẵn kiểu dữ liệu, tự bóc envelope, có sẵn hàm xác thực
> chữ ký webhook. Xem README của package. Phần REST dưới đây dành cho ngôn ngữ
> khác hoặc khi cần gọi trực tiếp.
>
> ```ts
> import { GpmPay } from "@gpmpay/sdk";
> const client = new GpmPay({ apiToken: process.env.GPMPAY_API_TOKEN! });
> const { data } = await client.transactions.list({ type: "IN", limit: 10 });
> ```

## 1. Xác thực (Authentication)
Mọi request gắn header:

```
Authorization: Bearer <API_TOKEN>
```

API token có dạng `gpm_xxx_yyy`, người dùng tạo trong giao diện GPM Pay tại mục
**Tích hợp → API Tokens**. Khi tạo, chọn các scope cần dùng:

| Scope | Quyền |
|---|---|
| `transactions:read` | Đọc lịch sử giao dịch |
| `bank-accounts:read` | Đọc thông tin tài khoản ngân hàng |
| `webhooks:manage` | Tạo / sửa / xoá webhook |

> Token chỉ hiển thị đầy đủ **một lần** lúc tạo — yêu cầu người dùng lưu lại an toàn
> (biến môi trường `GPMPAY_API_TOKEN`, không hardcode vào source).

## 2. Lấy lịch sử giao dịch
`GET https://api.gpmpay.com/api/v1/transactions`

Query params (tuỳ chọn):

| Param | Kiểu | Mô tả |
|---|---|---|
| `page` | number | Trang (mặc định 1) |
| `limit` | number | Số dòng/trang (mặc định 20) |
| `type` | `IN` \| `OUT` | Tiền vào / tiền ra |
| `source` | `REAL` \| `SIMULATED` | Giao dịch thật / giả lập |
| `bankAccountId` | string | Lọc theo tài khoản |
| `startDate`, `endDate` | ISO8601 | Khoảng thời gian |
| `search` | string | Tìm theo nội dung / mã tham chiếu |

Ví dụ:

```bash
curl -s "https://api.gpmpay.com/api/v1/transactions?type=IN&limit=10" \
  -H "Authorization: Bearer $GPMPAY_API_TOKEN"
```

Mỗi giao dịch gồm các field chính: `id`, `type`, `amount`, `transferContent`,
`referenceCode`, `counterName`, `counterAccount`, `counterBankBin`,
`balanceAfter`, `transactionTime`, `source`.

> Tên field ở đây **khác** tên field trong payload webhook — xem bảng đối chiếu
> ở mục 4 trước khi dùng chung một kiểu dữ liệu cho cả hai.

```js
// Node.js (fetch)
const res = await fetch(`${process.env.GPMPAY_API_URL}/transactions?type=IN&limit=10`, {
  headers: { Authorization: `Bearer ${process.env.GPMPAY_API_TOKEN}` },
});
const { data } = await res.json();
console.log(data.data, data.meta);
```

## 3. Tạo webhook nhận giao dịch realtime
`POST https://api.gpmpay.com/api/v1/webhook-settings`

Body:

```json
{
  "scope": "ALL",
  "driver": "HTTP",
  "url": "https://your-app.com/webhooks/gpmpay",
  "authorizationType": "HMAC",
  "authorizationSecret": "<chuoi-bi-mat-ngau-nhien>",
  "fireOnSimulated": true
}
```

- `scope`: `ALL` (mọi tài khoản) hoặc `SPECIFIC` (kèm `bankAccountIds: string[]`).
- `authorizationType`: `HMAC` (khuyến nghị), `API_KEY`, hoặc `NONE`.
- `authorizationSecret`: bí mật dùng để ký HMAC — lưu lại để verify ở bước 5.

```bash
curl -s -X POST "https://api.gpmpay.com/api/v1/webhook-settings" \
  -H "Authorization: Bearer $GPMPAY_API_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"scope":"ALL","driver":"HTTP","url":"https://your-app.com/webhooks/gpmpay","authorizationType":"HMAC","authorizationSecret":"REPLACE_ME"}'
```

> **Endpoint phải sống trước khi gọi API này.** GPM Pay ping thử `url` ngay lúc tạo
> (và lúc sửa `url` / secret / kiểu xác thực). Không trả 2xx trong 5 giây thì webhook
> **không được lưu**, API trả `400`. Hãy deploy handler trước — dev local thì mở
> tunnel (ngrok / cloudflared) rồi mới đăng ký.

## 4. Payload webhook gửi về
Khi có giao dịch, GPM Pay POST JSON tới `url`. Payload luôn đủ **14 field** dưới
đây — field không có dữ liệu trả `null` chứ không bị lược bỏ:

```json
{
  "id": "b6fec789-f2bd-445d-88e5-86aee24f35c8",
  "source": "REAL",
  "content": "Test doi ung",
  "gateway": "MB",
  "subAccount": null,
  "accumulated": null,
  "counterName": null,
  "transferType": "in",
  "accountNumber": "0342971086",
  "referenceCode": "6LPJSYNrZf7qspvd",
  "counterAccount": "6987",
  "counterBankBin": "970427",
  "transferAmount": 9720,
  "transactionDate": "2026-09-12T16:42:21.013Z"
}
```

| Field | Kiểu | Ghi chú |
|---|---|---|
| `id` | string (uuid) | ID giao dịch — **dùng làm khoá idempotency** |
| `source` | `REAL` \| `SIMULATED` | `SIMULATED` là giao dịch tạo từ trang Giả lập |
| `content` | string | Nội dung chuyển khoản, **cắt còn tối đa 100 ký tự** |
| `gateway` | string | Mã ngân hàng: `MB`, `VCB`, `ACB`… |
| `subAccount` | string \| null | Tài khoản phụ |
| `accumulated` | number \| null | Số dư sau giao dịch — nhiều ngân hàng không gửi |
| `counterName` | string \| null | Tên chủ tài khoản đối ứng |
| `transferType` | `in` \| `out` | Chữ **thường** |
| `accountNumber` | string | Số tài khoản nhận tiền |
| `referenceCode` | string | Mã giao dịch của ngân hàng; giao dịch giả lập có tiền tố `SIM-` |
| `counterAccount` | string \| null | Số tài khoản đối ứng |
| `counterBankBin` | string \| null | Mã BIN NAPAS (6 số); ngân hàng ngoài NAPAS trả mã CITAD 8 ký tự |
| `transferAmount` | number | VND, không phải xu |
| `transactionDate` | string (ISO 8601) | Thời điểm ngân hàng ghi nhận |

Giao dịch giả lập cùng shape, chỉ khác `source` và các field đối ứng thường rỗng:

```json
{
  "id": "bd740801-869c-4377-882b-935ba1a48920",
  "source": "SIMULATED",
  "content": "CHuyen tien DH2BN4JA 21dwef 312",
  "gateway": "MB",
  "subAccount": null,
  "accumulated": null,
  "counterName": null,
  "transferType": "in",
  "accountNumber": "0342971086",
  "referenceCode": "SIM-01M2B6RDDVBFP19906WS7TBE67",
  "counterAccount": null,
  "counterBankBin": null,
  "transferAmount": 112233,
  "transactionDate": "2026-09-12T16:21:09.171Z"
}
```

> **Tên field ở webhook KHÁC với `GET /transactions`.** Đừng dùng chung một kiểu
> dữ liệu cho hai nơi:
>
> | Webhook | REST `/transactions` |
> |---|---|
> | `content` | `transferContent` |
> | `transferAmount` | `amount` |
> | `transferType` — `in` / `out` | `type` — `IN` / `OUT` |
> | `transactionDate` | `transactionTime` |
> | `accumulated` | `balanceAfter` |

Endpoint của bạn phải trả HTTP **2xx** để xác nhận đã nhận. Nếu lỗi, GPM Pay tự retry
(backoff luỹ thừa, tối đa 6 lần).

### Gói tin xác minh
Lần ping lúc tạo / sửa webhook dùng đúng shape trên, thêm `"test": true` và các giá
trị cố định: `id` toàn số 0, `gateway: "GPMPAY"`, `referenceCode: "GPMPAY-VERIFY"`,
`transferAmount: 1`, `source: "SIMULATED"`. Verify chữ ký như bình thường (nó được ký
trên chính body này), rồi trả 200 và **thoát sớm** — đừng ghi nhận đơn hàng:

```js
if (body.test) return res.sendStatus(200);
```

## 5. Xác thực chữ ký HMAC-SHA256
Mỗi webhook kèm header:

```
X-GPMPay-Signature: t=<unix_seconds>,v1=<hex_digest>
```

Cách verify: tính `HMAC_SHA256(secret, "${t}.${rawBody}")` rồi so sánh với `v1`,
đồng thời kiểm tra `t` lệch không quá 300 giây.

```js
// Express — nhớ dùng raw body (KHÔNG parse JSON trước khi verify)
import crypto from "crypto";

function verifyGpmpay(rawBody, signatureHeader, secret) {
  const parts = Object.fromEntries(
    signatureHeader.split(",").map((kv) => kv.split("=")),
  );
  const t = parseInt(parts.t, 10);
  if (Math.abs(Math.floor(Date.now() / 1000) - t) > 300) return false;

  const expected = crypto
    .createHmac("sha256", secret)
    .update(`${t}.${rawBody}`)
    .digest("hex");

  return crypto.timingSafeEqual(
    Buffer.from(expected, "hex"),
    Buffer.from(parts.v1, "hex"),
  );
}
```

## Gợi ý cho AI
- Đọc biến môi trường: `GPMPAY_API_URL=https://api.gpmpay.com/api/v1`, `GPMPAY_API_TOKEN`, `GPMPAY_WEBHOOK_SECRET`.
- Luôn verify HMAC trước khi xử lý webhook.
- GPM Pay **không** giữ trạng thái đơn hàng. Tự nhét mã đơn của bạn vào nội dung
  chuyển khoản, rồi parse `content` để map giao dịch về đơn trong hệ thống người dùng.
- Để test nhanh, dùng `source=SIMULATED` qua tính năng "Giả lập giao dịch" trong GPM Pay.
