import { createBuilder } from './.aspire/modules/aspire.mjs';

const builder = await createBuilder();

//#if (UseRedisCache)
// Add a Redis cache for the app to use.
const cache = await builder
    .addRedis("cache");

//#endif
// Run the Python FastAPI app and expose its HTTP endpoint externally.
const app = await builder
    .addUvicornApp("app", "./app", "main:app")
    .withUv()
    .withExternalHttpEndpoints()
//#if (UseRedisCache)
    .withReference(cache)
    .waitFor(cache)
//#endif
    .withHttpHealthCheck({ path: "/health" });

// Run the Vite frontend after the API and inject the API URL for local proxying.
const frontend = await builder
    .addViteApp("frontend", "./frontend")
    .withReference(app)
    .waitFor(app);

// Bundle the frontend build output into the API container for publish/deploy.
await app.publishWithContainerFiles(frontend, "./static");

await builder.build().run();
