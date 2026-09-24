import type { Config } from 'tailwindcss';

// Design tokens and primitives land in Task 016; this baseline only wires
// content detection and leaves the default theme untouched.
const config: Config = {
  content: ['./index.html', './src/**/*.{ts,tsx}'],
  theme: {
    extend: {},
  },
  plugins: [],
};

export default config;
