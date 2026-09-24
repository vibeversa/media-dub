import type { Decorator, Preview } from '@storybook/react';
import '../src/styles/index.css';

export const globalTypes = {
  theme: {
    name: 'Theme',
    description: 'Light/dark token theme',
    defaultValue: 'light',
    toolbar: {
      icon: 'circlehollow',
      items: [
        { value: 'light', title: 'Light' },
        { value: 'dark', title: 'Dark' },
      ],
      dynamicTitle: true,
    },
  },
  direction: {
    name: 'Direction',
    description: 'LTR/RTL layout direction',
    defaultValue: 'ltr',
    toolbar: {
      icon: 'transfer',
      items: [
        { value: 'ltr', title: 'LTR' },
        { value: 'rtl', title: 'RTL' },
      ],
      dynamicTitle: true,
    },
  },
  locale: {
    name: 'Locale',
    description: 'Locale for RelativeTime/CostDisplay',
    defaultValue: 'en',
    toolbar: {
      icon: 'globe',
      items: [
        { value: 'en', title: 'English' },
        { value: 'es', title: 'Español' },
        { value: 'ar', title: 'العربية (RTL)' },
      ],
      dynamicTitle: true,
    },
  },
};

const withGlobals: Decorator = (Story, context) => {
  const theme = (context.globals['theme'] as string | undefined) ?? 'light';
  let direction = (context.globals['direction'] as string | undefined) ?? 'ltr';
  const locale = (context.globals['locale'] as string | undefined) ?? 'en';
  if (locale === 'ar' && direction === 'ltr') {
    direction = 'rtl';
  }
  document.documentElement.setAttribute('data-theme', theme);
  document.documentElement.setAttribute('dir', direction);
  document.documentElement.setAttribute('lang', locale);
  return Story();
};

const preview: Preview = {
  decorators: [withGlobals],
  parameters: {
    layout: 'padded',
  },
};

export default preview;
