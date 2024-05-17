*** Settings ***
Suite Setup                   Setup
Suite Teardown                Teardown
Test Setup                    Reset Emulation
Test Teardown                 Test Teardown
Test Timeout                  10 seconds
Resource                      ${RENODEKEYWORDS}

*** Variables ***
${SCRIPT}                     ${CURDIR}/test.resc
${UART}                       sysbus.usart6


*** Keywords ***
Load Script
    Execute Script            ${SCRIPT}
    Create Terminal Tester    ${UART}


*** Test Cases ***
Should Run Test Case
    Load Script
    Start Emulation
    Wait For Line On Uart     INFO:<SPI Access Test>
    Wait For Line On Uart     INFO:<ADAU1452: CoreStatus: 0x0000>
    Wait For Line On Uart     EXIT:<done>
