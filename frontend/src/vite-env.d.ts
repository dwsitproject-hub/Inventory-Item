/// <reference types="vite/client" />

// Gives TypeScript the shape of import.meta.env, which the sign-in page uses to show the
// local-development credential hint only in a dev build (AR-14). It is false in the
// production bundle that ships to staging.
