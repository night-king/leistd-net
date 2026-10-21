import { bootstrapApplication } from '@angular/platform-browser';

import { App } from './app/app';
import { appConfig } from './app/app.config';

bootstrapApplication(App, appConfig)
  .then(() => {
    const preloader = document.getElementById('preloader');
    if (preloader) {
      preloader.classList.add('opacity-0');
      setTimeout(() => preloader.remove(), 300);
    }
  })
  .catch((err) => console.error(err));
