// @ts-check
import { defineConfig } from 'astro/config';
import starlight from '@astrojs/starlight';
import starlightLinksValidator from 'starlight-links-validator';

export default defineConfig({
  site: 'https://mckcieply.github.io',
  base: '/Auth-Core',
  integrations: [
    starlight({
      title: 'Auth-Core',
      favicon: '/favicon.svg',
      defaultLocale: 'root',
      locales: {
        root: { label: 'English', lang: 'en' },
        pl: { label: 'Polski', lang: 'pl' },
      },
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/MckCieply/Auth-Core' }],
      editLink: { baseUrl: 'https://github.com/MckCieply/Auth-Core/edit/main/site/' },
      pagination: false,
      lastUpdated: false,
      components: {
        Footer: './src/components/Footer.astro',
        Hero: './src/components/Hero.astro',
      },
      customCss: [
        '@fontsource/space-grotesk/400.css',
        '@fontsource/space-grotesk/500.css',
        '@fontsource/space-grotesk/700.css',
        '@fontsource/ibm-plex-mono/400.css',
        '@fontsource/ibm-plex-mono/500.css',
        './src/styles/theme.css',
      ],
      sidebar: [
        { slug: 'fit', label: 'Is it for me?', translations: { pl: 'Czy to dla mnie?' } },
        { slug: 'compare', label: 'Comparison', translations: { pl: 'Porównanie' } },
        { slug: 'license', label: 'License', translations: { pl: 'Licencja' } },
        { slug: 'versions', label: 'Versions', translations: { pl: 'Wersje' } },
      ],
      plugins: [starlightLinksValidator()],
    }),
  ],
});
