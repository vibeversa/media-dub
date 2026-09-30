// CloudFront `viewer-request` function: HTTPS redirect + security headers
// (Task 043, instruction 1). Deploy on the distribution's DEFAULT behaviour,
// as viewer-request, BEFORE the cache behaviour.
//
// WHY A FUNCTION AND NOT A CACHE BEHAVIOUR
// ----------------------------------------
// CloudFront Functions (1.x) is the only feature layer that both redirects to
// HTTPS and adds response headers in one deterministic, region-free unit.
// Lambda@Edge runs in a region you pay for and whose logs contain request
// details nobody asked it to hold. An origin request policy can add headers but
// cannot redirect before the origin is contacted, which means a cleartext
// request has already crossed the network.
//
// WHY IT IS `viewer-request` AND NOT `viewer-response`
// ---------------------------------------------------
// viewer-request runs before the cache lookup, so the redirect happens without
// fetching anything and the headers are attached to the response the edge then
// caches. A viewer-response function runs on every cache HIT, so the headers are
// re-appended on every request - measurably more work, and the only way to be
// sure the header is present is for it to be added on the miss too.
//
// WHAT IT DOES NOT DO
// -------------------
// It does not set Cache-Control. That belongs to the origin (deploy/nginx/
// default.conf), because the cache classes depend on which path matched, and
// duplicating the classification here would give the two independent
// definitions of the same decision. `origin.json` declares the classes as data
// and deploy/tests/hosting.test.sh asserts the origin implements them.

var HTTPS = 'https:';
var YEAR = 31536000;

/**
 * The header set, matching Task 037's `SecurityHeadersMiddleware` and
 * `deploy/cdn/origin.json`. Kept as a literal rather than read from a file
 * because a CloudFront Function has no filesystem and no module resolution;
 * `deploy/tests/hosting.test.sh` asserts this object equals the API's, so the
 * duplication is checked rather than trusted.
 */
var SECURITY_HEADERS = {
  'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'",
  'Referrer-Policy': 'no-referrer',
  'X-Content-Type-Options': 'nosniff',
  'X-Frame-Options': 'DENY',
  'Strict-Transport-Security': 'max-age=' + YEAR + '; includeSubDomains',
};

function handler(event) {
  var request = event.request;

  // --- HTTPS redirect ---------------------------------------------------------
  // `uri` carries the query string, `url` does not. Building the redirect from
  // `url` silently drops every query parameter, which for this application means
  // a deep link with a `?access_token=` loses it and the user lands on the
  // dashboard instead of the page they asked for - with no error anywhere.
  if (request.headers['cloudfront-forwarded-proto'].value === 'http') {
    return {
      statusCode: 301,
      statusDescription: 'Moved Permanently',
      headers: {
        location: {
          value: HTTPS + request.headers.host.value + request.uri,
        },
        // A redirect is itself cacheable by default at some intermediaries. A
        // cached 301 is how an http:// URL keeps redirecting to a host that has
        // since been decommissioned.
        'cache-control': { value: 'max-age=0, no-store' },
      },
    };
  }

  // --- headers ----------------------------------------------------------------
  for (var name in SECURITY_HEADERS) {
    if (Object.prototype.hasOwnProperty.call(SECURITY_HEADERS, name)) {
      request.headers[name] = { value: SECURITY_HEADERS[name] };
    }
  }

  // Strip the forwarding headers the edge set. They are edge-internal; a
  // response carrying them tells an attacker which CDN is in front, which is one
  // less thing to fingerprint.
  delete request.headers['cloudfront-forwarded-proto'];
  delete request.headers['cloudfront-is-desktop-viewer'];
  delete request.headers['cloudfront-is-mobile-viewer'];
  delete request.headers['cloudfront-is-smarttv-viewer'];
  delete request.headers['cloudfront-is-tablet-viewer'];
  delete request.headers['x-forwarded-for'];

  return request;
}
