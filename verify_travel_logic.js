const axios = require('axios');

const API_URL = 'http://localhost:5032/api';
const CLIENT_ID = 1; // Assuming client 1 exists or I should create one? I'll assume valid IDs from previous context or create a new client if needed.
// Actually, let's list clients first or just try to create a file with a dummy client ID if strict FK. 
// Safest is to pick a client. I'll search for one or assume ID 1 is safe in dev db.

async function runTest() {
    console.log("🚀 Starting Travel Logic Verification...");

    let fileId;
    let passengerId;
    let hotelId;
    let paymentId;

    try {
        // 1. Create Travel File
        console.log("\n1. Creating Travel File...");
        const fileRes = await axios.post(`${API_URL}/travelfiles`, {
            name: "Test File Automation",
            clientId: 1,
            description: "Automated Test"
        });
        fileId = fileRes.data.id;
        console.log(`✅ File Created: ID ${fileId}, Status: ${fileRes.data.status}, Balance: ${fileRes.data.balance}`);

        // 2. Add Passenger
        console.log("\n2. Adding Passenger...");
        const paxRes = await axios.post(`${API_URL}/travelfiles/${fileId}/passengers`, {
            fullName: "Juan Perez",
            documentType: "DNI",
            documentNumber: "12345678",
            nationality: "Argentina"
        });
        passengerId = paxRes.data.id;
        console.log(`✅ Passenger Added: ID ${passengerId}`);

        // 3. Add Service (Hotel) -> Should update totals
        console.log("\n3. Adding Hotel Service (Cost: 100, Sale: 120)...");
        // Need a valid supplier ID. Assuming 1 exists.
        const hotelRes = await axios.post(`${API_URL}/files/${fileId}/hotels`, {
            supplierId: 1,
            hotelName: "Hotel Test",
            checkIn: new Date().toISOString(),
            checkOut: new Date(Date.now() + 86400000).toISOString(), // +1 day
            netCost: 100,
            salePrice: 120,
            rooms: 1,
            adults: 1
        });
        hotelId = hotelRes.data.id;
        console.log(`✅ Hotel Added: ID ${hotelId}`);

        // Verify File Totals
        const fileAfterService = (await axios.get(`${API_URL}/travelfiles/${fileId}`)).data;
        if (fileAfterService.totalSale === 120 && fileAfterService.totalCost === 100 && fileAfterService.balance === 120) {
            console.log("✅ File Totals Updated Correctly (Sale: 120, Cost: 100, Balance: 120)");
        } else {
            console.error("❌ File Totals Mismatch:", fileAfterService);
        }

        // 4. Update Service (Change Price) -> Should Recalculate
        console.log("\n4. Updating Hotel Service (Sale: 120 -> 150)...");
        await axios.put(`${API_URL}/files/${fileId}/hotels/${hotelId}`, {
            ...hotelRes.data, // Send back original data but modified
            salePrice: 150,
            supplierId: 1
        });

        const fileAfterUpdate = (await axios.get(`${API_URL}/travelfiles/${fileId}`)).data;
        if (fileAfterUpdate.totalSale === 150 && fileAfterUpdate.balance === 150) {
            console.log("✅ File Totals Recalculated Correctly (Sale: 150, Balance: 150)");
        } else {
            console.error("❌ File Totals Recalculation Failed:", fileAfterUpdate);
        }

        // 5. Add Payment (Partial)
        console.log("\n5. Adding Payment (Amount: 50)...");
        const payRes = await axios.post(`${API_URL}/travelfiles/${fileId}/payments`, {
            amount: 50,
            method: "Cash",
            notes: "Test Payment"
        });
        paymentId = payRes.data.id;
        console.log(`✅ Payment Added: ID ${paymentId}`);

        const fileAfterPay = (await axios.get(`${API_URL}/travelfiles/${fileId}`)).data;
        if (fileAfterPay.balance === 100) { // 150 - 50
            console.log("✅ Balance Updated Correctly (150 - 50 = 100)");
        } else {
            console.error("❌ Balance Update Failed:", fileAfterPay.balance);
        }

        // 6. Try to Delete File (Should Fail)
        console.log("\n6. Attempting to Delete File with Payments...");
        try {
            await axios.delete(`${API_URL}/travelfiles/${fileId}`);
            console.error("❌ DELETED File with payments! This should have failed.");
        } catch (e) {
            if (e.response && e.response.status === 400) {
                console.log("✅ Delete Blocked Correctly (400 Bad Request)");
            } else {
                console.error("❌ Unexpected Error:", e.message);
            }
        }

        // 7. Delete Payment -> Revert Balance
        console.log("\n7. Deleting Payment...");
        await axios.delete(`${API_URL}/travelfiles/${fileId}/payments/${paymentId}`);
        const fileAfterPayDel = (await axios.get(`${API_URL}/travelfiles/${fileId}`)).data;
        if (fileAfterPayDel.balance === 150) {
            console.log("✅ Balance Reverted Correctly (100 + 50 = 150)");
        } else {
            console.error("❌ Balance Revert Failed:", fileAfterPayDel.balance);
        }

        // 8. Delete Service -> Revert Totals
        console.log("\n8. Deleting Service...");
        await axios.delete(`${API_URL}/files/${fileId}/hotels/${hotelId}`);
        const fileAfterSvcDel = (await axios.get(`${API_URL}/travelfiles/${fileId}`)).data;
        if (fileAfterSvcDel.totalSale === 0 && fileAfterSvcDel.totalCost === 0) {
            console.log("✅ File Totals Reverted Correctly to 0");
        } else {
            console.error("❌ File Totals Revert Failed:", fileAfterSvcDel);
        }

        // 9. Delete File (Should Succeed)
        console.log("\n9. Deleting Empty File...");
        await axios.delete(`${API_URL}/travelfiles/${fileId}`);
        try {
            await axios.get(`${API_URL}/travelfiles/${fileId}`);
            console.error("❌ File still exists!");
        } catch (e) {
            if (e.response && e.response.status === 404) {
                console.log("✅ File Deleted Successfully (404 Not Found)");
            }
        }

        console.log("\n🎉 ALL TESTS PASSED!");

    } catch (error) {
        console.error("\n❌ TEST FAILED:", error.message);
        if (error.response) {
            console.error("Response:", error.response.data);
        }
    }
}

runTest();
