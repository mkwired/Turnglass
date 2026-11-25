import tsParser from '@typescript-eslint/parser'
import vue from 'eslint-plugin-vue'
import vueParser from 'vue-eslint-parser'

const preserveContentWhitespace = ['pre', 'textarea', 'blockquote', 'span', 'strong', 'code', 'a', 'RouterLink']

export default [
  {
    ignores: ['dist/**', 'coverage/**', 'playwright-report/**', 'test-results/**'],
  },
  {
    files: ['**/*.vue'],
    languageOptions: {
      parser: vueParser,
      parserOptions: {
        parser: tsParser,
        ecmaVersion: 'latest',
        sourceType: 'module',
      },
    },
    plugins: { vue },
    rules: {
      'vue/block-order': ['error', { order: ['template', 'script', 'style'] }],
      'vue/max-attributes-per-line': ['error', { singleline: 1, multiline: 1 }],
      'vue/first-attribute-linebreak': ['error', { singleline: 'ignore', multiline: 'below' }],
      'vue/html-closing-bracket-newline': ['error', { singleline: 'never', multiline: 'always' }],
      'vue/html-indent': ['error', 2],
      'vue/padding-line-between-tags': ['error', [
        { blankLine: 'always', prev: '*', next: '*' },
      ]],
      // Preserve literal log content and spacing within inline text, such as "50%".
      'vue/singleline-html-element-content-newline': ['error', {
        ignoreWhenNoAttributes: false,
        externalIgnores: preserveContentWhitespace,
      }],
      'vue/multiline-html-element-content-newline': ['error', {
        ignores: preserveContentWhitespace,
      }],
    },
  },
]
