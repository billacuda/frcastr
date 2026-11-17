// ESLint, for the "ESLint Security Scan" workflow (code scanning). Security rules only: the browser
// scripts under wwwroot are classic scripts (no modules, no bundler) and aren't style-linted. The
// workflow installs the pinned packages itself; there's no package.json.
import security from "eslint-plugin-security";
import noUnsanitized from "eslint-plugin-no-unsanitized";

const ESCAPERS = ["escHtml", "esc"];

export default [
    {
        // Vendored libraries are upstream code; build output and installers aren't source.
        ignores: ["**/wwwroot/lib/**", "**/bin/**", "**/obj/**", "publish/**", "**/node_modules/**", "**/ClientApp/**", "tests/**"],
    },
    {
        files: ["src/frcastr.Web/wwwroot/**/*.js"],
        languageOptions: {
            ecmaVersion: "latest",
            sourceType: "script",
        },
        plugins: {
            security,
            "no-unsanitized": noUnsanitized,
        },
        rules: {
            ...security.configs.recommended.rules,
            // Flags every obj[key] lookup: noise in scripts that index their own maps.
            "security/detect-object-injection": "off",
            // innerHTML, insertAdjacentHTML, document.write, and the like with unescaped values.
            // The escapers the scripts use: escHtml and esc.
            "no-unsanitized/method": ["error", { escape: { methods: ESCAPERS } }],
            "no-unsanitized/property": ["error", { escape: { methods: ESCAPERS } }],
            // Code from strings.
            "no-eval": "error",
            "no-implied-eval": "error",
            "no-new-func": "error",
            "no-script-url": "error",
        },
    },
];
