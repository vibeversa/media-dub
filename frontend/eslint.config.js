import js from '@eslint/js';
import reactHooks from 'eslint-plugin-react-hooks';
import reactRefresh from 'eslint-plugin-react-refresh';
import tseslint from 'typescript-eslint';

// Zero-warning policy: `npm run lint` uses --max-warnings=0, so every rule
// below is either error or off. The generated API client is owned by Task 014
// (hermetic generator + drift gate) and stays out of lint scope.
export default tseslint.config(
  {
    ignores: ['dist/**', 'node_modules/**', 'coverage/**', 'src/api/generated/**'],
  },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ['src/**/*.{ts,tsx}'],
    plugins: {
      'react-hooks': reactHooks,
      'react-refresh': reactRefresh,
    },
    rules: {
      '@typescript-eslint/no-explicit-any': 'error',
      'react-hooks/rules-of-hooks': 'error',
      'react-hooks/exhaustive-deps': 'error',
      'react-refresh/only-export-components': ['error', { allowConstantExport: true }],
      // All runtime env access funnels through src/lib/env.ts so missing or
      // invalid VITE_* values fail fast in one place. Direct import.meta.env
      // reads anywhere else are a defect.
      'no-restricted-syntax': [
        'error',
        {
          selector: "MemberExpression[object.type='MetaProperty'][object.meta.name='import']",
          message: 'Read environment only via src/lib/env.ts (getEnv()/tryGetEnv()).',
        },
      ],
    },
  },
  {
    files: ['src/lib/env.ts'],
    rules: {
      'no-restricted-syntax': 'off',
    },
  },
  {
    files: ['**/*.{js,mjs,cjs}'],
    ...tseslint.configs.disableTypeChecked,
  },
);
