# TravelTriangle prospect demonstration

Synthetic inventory inspired by public holiday package descriptions. This is not an affiliation, live TravelTriangle supplier connection or confirmed booking system.

Publish this plugin into the host's configured plugin directory and set the AppHost local `Parameters:customer-config-path` to this directory. Aspire supplies `ConnectionStrings:vertical-db` to both .NET processes. The plugin creates its own initial PostgreSQL schema; OpenWA's tables and framework session tables remain separate.

This directory owns customer-specific branding, prompt and data. `travel-scenario.json` is the authoritative priced offer fixture. `Data/` overrides the older catalog/policy/media tools to prevent bundled agency assumptions from appearing. Update both when changing the scenario. The bundled plugin data remains available when no override exists, so a new customer's partial override deliberately inherits defaults.

## Presenter journey

1. From the second WhatsApp account: “I’m Demo Family. Kerala for 5 days from Ahmedabad, starting 12 December 2026. Two adults and one child aged 8. Total budget ₹70,000, 3-star hotels, no houseboat. Compare both demo quotes.”
2. Show persisted inquiry and two expert quotes: **₹47,250 fixed**, **₹52,500 custom**. These are family totals including 5% demo tax. Fixed inventory has six seeded traveller slots on 12 December; custom inventory has no numeric available-slot claim.
3. Ask: “Show me photos of that tour.” Illustrative Kerala images come from `KERALA-DEMO`. They are destination illustrations, not evidence of a particular hotel or supplier. Older audio/video/brochure data are disabled for this customer.
4. In OpenWA Conversations choose **Take over**. Send: “I have noted a preference for 4-star hotels and a houseboat experience.” Show that the assistant stays paused for new customer messages.
5. Choose **Resume bot**. From the second account: “Please save those preferences and revise the fixed quote.” The new fixed quote is **₹63,000**, with an immutable earlier quote history. No slot is held.
6. Restart only WebApi and McpHost through Aspire, wait for health, then ask: “What hotel preference, houseboat option and quote did we save?” The plugin context provider retrieves the records from PostgreSQL; native Agent Framework sessions supply conversation history.
7. From the second account, ask to hold the saved fixed quote. The assistant presents the exact quote, total, party and date plus `APPROVE <code>` / `DECLINE <code>`. No slots are held yet. The traveller must send the exact code; a normal yes or an operator note does not approve it.
8. To demonstrate persistence, take over/resume and restart WebApi/McpHost before approval. Then approve from the second account: exactly one 15-minute demo hold should appear. Its absolute/local expiry is returned. Earlier quote versions remain intact. Releasing/expiry restores capacity; an expired replay never renews it.
9. Ask to pay the full quote on an active hold. A separate native approval authorizes a 30-minute checkout window and one Razorpay-hosted TEST payment link. Approve that code, open the returned URL and complete the test payment. The plugin verifies the signed webhook and provider API before persisting one demo booking and notifying WhatsApp. A late payment needs operator reconciliation; it never silently reacquires expired capacity. Human takeover pauses automated confirmation until resume.

## Pricing contract

For the fixed offer: adults ₹18,000 each; children aged 2–17 ₹9,000 each; infants under 2 have zero base price. Four-star supplement ₹3,000 per traveller; houseboat supplement ₹2,000 per traveller. Supplements apply to infants too in this explicitly synthetic fixture. Tax is 5% of the subtotal, rounded to two decimal places away from zero. Flights/trains and unstated meals are excluded. The total family budget is recorded but does not change the calculated price.

Each quote stores the requirements, offer/inventory version, component prices, inclusions/exclusions and source inquiry revision. Requirement changes clear the selected quote; older quotes remain historical. A newly calculated quote becomes the selected proposal, including when two proposals are calculated in sequence for comparison. This selection does not express traveller acceptance or a reservation.

## Reset and rehearsal

Use a fresh test chat or increment `AGENTFORGE_CONFIG_VERSION` in `customer-profile.json`, then restart both .NET resources. Changing the version intentionally isolates the customer journey AND native conversation namespace; old records remain stored. **Do not truncate transport queues, webhook/send deduplication records, operator controls or production chat history to reset the demo.** This is namespace isolation, not deletion or retention cleanup.

Dates are fixed demo data and must be future-dated when rehearsing later. Bump the scenario version when changing offer prices/rules and the customer configuration version when intentionally starting a fresh demonstration. Preserve the old configuration if earlier records must be accessed again. Do not switch configuration while conversations or holds are active. Release or let demo holds expire first. Changing customer/inventory configuration creates an isolated demonstration inventory partition; it is not a production inventory-reset mechanism.

MCP tools require the host's signed identity headers. Model-supplied phone numbers and references cannot select another customer's data. Ordinary unauthenticated MCP Inspector requests now receive 401; use the signed host client for these tools. The generic identity protocol is documented in the Core batch report.
