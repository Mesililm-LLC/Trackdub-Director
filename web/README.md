# web — Trackdub Director frontend

React + Vite + TypeScript. One screen: header with key/model/QA summary, line list with window/estimate/verdict, expandable attempts.

- `npm install`
- `npm run dev` — Vite on http://localhost:5173, proxies `/api` to http://localhost:5103
- `npm run build` — emits to `src/Trackdub.Director.Server/wwwroot`
- `npm test` — Vitest + Testing Library
